"""
core/diagnostics.py
-------------------
Pipeline automatico "Diagnosticar auriculares".

Secuencia (cada paso reporta progreso y tolera fallos parciales):
    1. Snapshot BLE del ultimo escaneo (nombre, RSSI, fabricante, UUIDs).
    2. Bateria via GATT (con timeout: muchos TWS no la exponen).
    3. Tests de audio en hilo de trabajo: RMS L/R, latencia, jitter
       y continuidad (deteccion de dropouts).
    4. Analitica historica + Device Quality Score + perfil de firmware.
    5. Reporte tecnico (PDF/HTML) y sesion completa en sessions/
       (session.json, ble_log.json, waveform.wav, diagnostics.pdf).

El runner vive en el hilo principal de Qt y orquesta trabajo asincrono
(motor BLE) y bloqueante (audio, en threading.Thread) solo con senales.
"""

from __future__ import annotations

import logging
import threading

from PySide6.QtCore import QObject, QTimer, Signal

from audio import audio_test, dropout_test, latency_test
from ble.firmware_profiler import profile_firmware
from core import analytics, scoring
from core.config_manager import PROJECT_ROOT
from core.report_generator import generate_report
from core.session_recorder import SessionRecorder
from core.versioning import engine_metadata

logger = logging.getLogger("lino.core.diagnostics")

BATTERY_TIMEOUT_MS = 20_000
JITTER_RUNS = 3
EXPORTS_DIR = PROJECT_ROOT / "exports"


class DiagnosticRunner(QObject):
    """Ejecuta el pipeline completo para un dispositivo seleccionado."""

    progress = Signal(str)        # texto de avance para la UI
    finished = Signal(object)     # dict del reporte (incluye report_path)
    failed = Signal(str)

    _audio_done = Signal(object)  # interno: resultados del hilo de audio

    def __init__(self, app, device, parent: QObject | None = None):
        """
        Args:
            app: AppManager (motor BLE + base de datos).
            device: DeviceInfo del dispositivo a diagnosticar.
        """
        super().__init__(parent)
        self._app = app
        self._device = device
        self._report: dict = {
            # Versionado: sin esto, reportes de motores distintos no son
            # comparables cuando los algoritmos evolucionen.
            "meta": engine_metadata(app.config.get("version")),
            "device": {
                "name": device.name,
                "mac": device.mac,
                "rssi": device.rssi,
                "manufacturer": device.manufacturer,
                "uuids": device.uuids,
            }
        }
        self._battery_timer: QTimer | None = None
        self._running = False
        self._audio_done.connect(self._on_audio_done)

    # ------------------------------------------------------------------
    # Paso 1-2: BLE
    # ------------------------------------------------------------------
    def start(self) -> None:
        if self._running:
            return
        self._running = True
        mac = self._device.mac
        self.progress.emit(f"[1/5] Snapshot BLE de {self._device.name}")
        self.progress.emit("[2/5] Leyendo bateria GATT...")

        engine = self._app.ble_engine
        engine.battery_read.connect(self._on_battery)
        engine.request_battery(mac)

        # Timeout: si el dispositivo no responde, el pipeline continua.
        self._battery_timer = QTimer(self)
        self._battery_timer.setSingleShot(True)
        self._battery_timer.timeout.connect(self._on_battery_timeout)
        self._battery_timer.start(BATTERY_TIMEOUT_MS)

    def _on_battery(self, mac: str, level) -> None:
        if mac != self._device.mac:
            return  # lectura de otro dispositivo
        self._detach_battery()
        self._report["battery"] = level
        self._start_audio_phase()

    def _on_battery_timeout(self) -> None:
        logger.warning("Diagnostico: timeout leyendo bateria de %s", self._device.mac)
        self._detach_battery()
        self._report["battery"] = None
        self._start_audio_phase()

    def _detach_battery(self) -> None:
        try:
            self._app.ble_engine.battery_read.disconnect(self._on_battery)
        except RuntimeError:
            pass
        if self._battery_timer:
            self._battery_timer.stop()

    # ------------------------------------------------------------------
    # Paso 3: audio (hilo de trabajo, sd.wait es bloqueante)
    # ------------------------------------------------------------------
    def _start_audio_phase(self) -> None:
        self.progress.emit("[3/5] Tests de audio: RMS, latencia y jitter...")

        def worker():
            results: dict = {}
            try:
                results["rms"] = audio_test.measure_rms_balance()
                single = latency_test.measure_once()
                if single and single.valid:
                    results["latency"] = {
                        "latency_ms": single.latency_ms,
                        "confidence": single.confidence,
                        "classification": single.classification,
                    }
                jitter = latency_test.jitter_test(runs=JITTER_RUNS)
                if jitter:
                    results["jitter"] = {
                        "mean_ms": jitter.mean_ms,
                        "std_ms": jitter.std_ms,
                        "classification": jitter.classification,
                    }
                continuity = dropout_test.dropout_test(duration=4.0)
                if continuity:
                    results["continuity"] = {
                        "dropouts": continuity.dropouts,
                        "total_gap_ms": continuity.total_gap_ms,
                        "continuity_pct": continuity.continuity_pct,
                    }
            except Exception as exc:  # noqa: BLE001 - frontera de hilo
                logger.error("Fase de audio del diagnostico fallo: %s", exc)
            self._audio_done.emit(results)

        threading.Thread(target=worker, daemon=True).start()

    def _on_audio_done(self, results: dict) -> None:
        self._report.update(results)

        # Persistir mediciones validas (estamos en el hilo principal).
        if results.get("latency"):
            self._app.database.save_latency(
                latency_ms=results["latency"]["latency_ms"],
                confidence=results["latency"]["confidence"],
                classification=results["latency"]["classification"],
            )
        if results.get("jitter"):
            self._app.database.save_latency(
                latency_ms=results["jitter"]["mean_ms"],
                jitter_ms=results["jitter"]["std_ms"],
                classification=results["jitter"]["classification"],
            )
        self._finish()

    # ------------------------------------------------------------------
    # Pasos 4-5: analitica + reporte
    # ------------------------------------------------------------------
    def _finish(self) -> None:
        mac = self._device.mac
        self.progress.emit("[4/5] Analitica, score y perfil de firmware...")
        try:
            self._report["analytics"] = analytics.full_report_data(
                self._app.database, mac
            )
        except Exception as exc:  # noqa: BLE001
            logger.error("Analitica del diagnostico fallo: %s", exc)
            self._report["analytics"] = {}

        # Device Quality Score con los resultados frescos del pipeline.
        extras: dict = {}
        if self._report.get("rms"):
            extras["rms_diff_db"] = self._report["rms"]["diff_db"]
        if self._report.get("continuity"):
            extras["continuity_pct"] = self._report["continuity"]["continuity_pct"]
        try:
            self._report["quality_score"] = scoring.device_quality_score(
                self._app.database, mac, extras=extras
            )
        except Exception as exc:  # noqa: BLE001
            logger.error("Scoring fallo: %s", exc)

        # Perfil de firmware desde la huella BLE conocida.
        caps = self._report["analytics"].get("capabilities") or {}
        self._report["firmware"] = profile_firmware(
            self._device.manufacturer,
            self._device.uuids or caps.get("uuids"),
            caps.get("services"),
        )

        self.progress.emit("[5/5] Generando reporte y sesion...")
        path = generate_report(self._report, self._app.database, EXPORTS_DIR)
        self._report["report_path"] = str(path) if path else None

        # Sesion completa: artefacto de laboratorio autocontenido.
        try:
            session = SessionRecorder(mac)
            session.save_session_json(self._report)
            session.save_ble_log(self._app.database, mac)
            session.save_waveform(dropout_test.get_last_recording())
            session.attach_report(path)
            self._report["session_dir"] = str(session.session_dir)
        except Exception as exc:  # noqa: BLE001
            logger.error("No se pudo grabar la sesion: %s", exc)

        self._running = False
        logger.info("Diagnostico de %s completado (reporte: %s)", mac, path)
        self.finished.emit(self._report)
