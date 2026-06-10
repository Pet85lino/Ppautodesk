"""
core/session_recorder.py
------------------------
Grabador de sesiones de diagnostico (formato laboratorio).

Cada sesion produce una carpeta autocontenida:

    sessions/<timestamp>_<mac>/
    ├── session.json      -> reporte completo del pipeline
    ├── waveform.wav      -> ultima grabacion de audio (si existe)
    ├── diagnostics.pdf   -> reporte tecnico (o .html de fallback)
    ├── ble_log.json      -> eventos BLE + capacidades del dispositivo
    └── plots/            -> graficas adicionales (espectro, curvas...)

La sesion es el artefacto exportable/archivable de cada diagnostico.
"""

from __future__ import annotations

import json
import logging
import shutil
from datetime import datetime
from pathlib import Path

import numpy as np

from core.config_manager import PROJECT_ROOT

logger = logging.getLogger("lino.core.session")

SESSIONS_DIR = PROJECT_ROOT / "sessions"
SAMPLE_RATE = 48_000


class SessionRecorder:
    """Construye la carpeta de una sesion de diagnostico."""

    def __init__(self, mac: str, base_dir: Path | None = None):
        stamp = f"{datetime.now():%Y%m%d_%H%M%S}"
        safe_mac = mac.replace(":", "")
        self.session_dir = (base_dir or SESSIONS_DIR) / f"{stamp}_{safe_mac}"
        self.plots_dir = self.session_dir / "plots"
        self.plots_dir.mkdir(parents=True, exist_ok=True)
        logger.info("Sesion iniciada: %s", self.session_dir)

    # ------------------------------------------------------------------
    # Artefactos
    # ------------------------------------------------------------------
    def save_session_json(self, report: dict) -> Path | None:
        """session.json: el reporte completo del pipeline, serializado."""
        return self._write_json(self.session_dir / "session.json", report)

    def save_ble_log(self, db, mac: str, limit: int = 500) -> Path | None:
        """ble_log.json: eventos BLE + fingerprint del dispositivo."""
        try:
            rows = db._conn.execute(
                """SELECT event_type, detail, timestamp FROM ble_events
                   WHERE mac = ? ORDER BY id DESC LIMIT ?""",
                (mac, limit),
            ).fetchall()
        except Exception as exc:  # noqa: BLE001 - lectura defensiva
            logger.error("Error leyendo eventos BLE para la sesion: %s", exc)
            rows = []
        payload = {
            "mac": mac,
            "events": [
                {"type": r[0], "detail": r[1], "timestamp": r[2]} for r in rows
            ],
            "capabilities": db.get_fingerprint(mac),
        }
        return self._write_json(self.session_dir / "ble_log.json", payload)

    def save_waveform(self, samples, sample_rate: int = SAMPLE_RATE) -> Path | None:
        """waveform.wav: grabacion cruda del ultimo test de audio."""
        if samples is None or len(samples) == 0:
            return None
        try:
            from scipy.io import wavfile

            out_path = self.session_dir / "waveform.wav"
            data = np.asarray(samples, dtype=np.float32)
            wavfile.write(out_path, sample_rate, data)
            logger.info("Waveform guardado: %s", out_path)
            return out_path
        except (ImportError, OSError, ValueError) as exc:
            logger.error("Error guardando waveform: %s", exc)
            return None

    def attach_report(self, report_path: str | Path | None) -> Path | None:
        """Copia el reporte tecnico (PDF/HTML) dentro de la sesion."""
        if not report_path:
            return None
        src = Path(report_path)
        if not src.exists():
            return None
        try:
            dst = self.session_dir / f"diagnostics{src.suffix}"
            shutil.copy2(src, dst)
            return dst
        except OSError as exc:
            logger.error("Error copiando reporte a la sesion: %s", exc)
            return None

    def attach_plot(self, plot_path: str | Path | None) -> Path | None:
        """Mueve una grafica adicional a plots/."""
        if not plot_path:
            return None
        src = Path(plot_path)
        if not src.exists():
            return None
        try:
            dst = self.plots_dir / src.name
            shutil.copy2(src, dst)
            return dst
        except OSError as exc:
            logger.error("Error copiando grafica a la sesion: %s", exc)
            return None

    # ------------------------------------------------------------------
    def _write_json(self, path: Path, payload: dict) -> Path | None:
        try:
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(payload, fh, ensure_ascii=False, indent=2, default=str)
            return path
        except (OSError, TypeError) as exc:
            logger.error("Error escribiendo %s: %s", path.name, exc)
            return None
