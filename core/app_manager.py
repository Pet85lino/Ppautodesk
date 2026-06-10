"""
core/app_manager.py
-------------------
Orquestador central de la aplicacion.

Responsabilidades:
    * Cargar configuracion e inicializar logging.
    * Crear base de datos, motor BLE, estado central y bus de eventos.
    * Conectar las senales del motor a la persistencia y al bus (toda
      escritura SQLite ocurre en el hilo principal de Qt, via senales).
    * Apagado ordenado de todos los subsistemas.

La UI (dashboard) recibe una instancia de AppManager y nunca crea
recursos por su cuenta: separacion estricta UI / backend.
"""

from __future__ import annotations

import logging

from ble.ble_scanner import BLEEngine
from core.app_state import AppState
from core.config_manager import ConfigManager
from core.event_bus import EventBus
from core.logger import setup_logging
from core.perf_monitor import PerfMonitor
from database.database import DatabaseManager

logger = logging.getLogger("lino.core.app")


class AppManager:
    """Punto unico de acceso a los subsistemas de la suite."""

    def __init__(self):
        # 1. Configuracion + logging (antes que cualquier otro subsistema).
        self.config = ConfigManager.load()
        setup_logging(self.config.get("logging.level", "INFO"))
        logger.info(
            "%s v%s inicializando...",
            self.config.get("app_name"),
            self.config.get("version"),
        )

        # 2. Persistencia SQLite.
        self.database = DatabaseManager(self.config.database_path)

        # 3. Estado central y bus de eventos (desacople UI / backend).
        self.state = AppState()
        self.bus = EventBus()

        # 4. Motor BLE en hilo dedicado.
        self.ble_engine = BLEEngine(
            scan_duration=float(self.config.get("scan.duration_seconds", 4.0)),
            connect_timeout=float(
                self.config.get("battery.connect_timeout_seconds", 10.0)
            ),
            retry_attempts=int(self.config.get("ble.retry_attempts", 3)),
            backoff_base_s=float(self.config.get("ble.backoff_base_s", 1.0)),
        )

        # 5. Cableado: motor BLE -> estado + persistencia + bus.
        self.ble_engine.scan_finished.connect(self._on_scan_finished)
        self.ble_engine.battery_read.connect(self._on_battery_read)
        self.ble_engine.fingerprint_ready.connect(self._on_fingerprint)
        self.ble_engine.ble_event.connect(self._on_ble_event)
        self.ble_engine.raw_log_ready.connect(self._on_raw_log)
        self.ble_engine.engine_error.connect(self._on_engine_error)

    # ------------------------------------------------------------------
    # Ciclo de vida
    # ------------------------------------------------------------------
    def start(self) -> None:
        """Arranca los subsistemas en segundo plano."""
        self.ble_engine.start()

        # Monitor de memoria/rendimiento (opt-in: sesiones largas).
        self.perf_monitor: PerfMonitor | None = None
        if self.config.get("perf.monitor_enabled", False):
            self.perf_monitor = PerfMonitor(
                interval_s=float(self.config.get("perf.sample_interval_s", 60))
            )
            self.perf_monitor.start()

        self.database.save_log("INFO", "Aplicacion iniciada")

    def shutdown(self) -> None:
        """Apagado ordenado: motor BLE primero, base de datos al final."""
        logger.info("Apagando subsistemas...")
        if getattr(self, "perf_monitor", None):
            logger.info("Resumen perf: %s", self.perf_monitor.summary())
            self.perf_monitor.stop()
        self.database.save_log("INFO", "Aplicacion cerrada")
        self.ble_engine.shutdown()
        self.database.close()

    # ------------------------------------------------------------------
    # Persistencia de eventos BLE (ejecutado en el hilo principal de Qt)
    # ------------------------------------------------------------------
    def _on_scan_finished(self, devices: list) -> None:
        self.state.apply_scan(devices)
        self.database.save_scan_results(devices)
        self.bus.publish("ble.scan", devices)

    def _on_battery_read(self, mac: str, level) -> None:
        self.state.set_battery(mac, level)
        self.database.save_battery(mac, level)
        if level is not None:
            self.database.save_log("INFO", f"Bateria {mac}: {level}%")
        self.bus.publish("ble.battery", {"mac": mac, "level": level})

    def _on_fingerprint(self, mac: str, fingerprint: dict) -> None:
        # El promedio RSSI de la sesion completa vive en AppState.
        avg = self.state.avg_rssi(mac)
        if avg is not None:
            fingerprint["avg_rssi"] = avg
        self.database.save_fingerprint(fingerprint)
        self.database.save_log(
            "INFO",
            f"Fingerprint {mac}: {len(fingerprint.get('services', []))} servicios, "
            f"codecs {fingerprint.get('codecs', [])}",
        )
        self.bus.publish("ble.fingerprint", fingerprint)

    def _on_ble_event(self, mac: str, event_type: str, detail: str) -> None:
        self.database.save_ble_event(mac, event_type, detail)
        self.bus.publish("ble.event", {"mac": mac, "type": event_type, "detail": detail})

    def _on_raw_log(self, raw_log: dict) -> None:
        saved = self.database.save_raw_log(raw_log)
        self.database.save_log(
            "INFO",
            f"Raw BLE log: {raw_log.get('total_packets', 0)} paquetes "
            f"({saved} persistidos) de {raw_log.get('devices', 0)} dispositivo(s)",
        )
        self.bus.publish("ble.raw_log", raw_log)

    def _on_engine_error(self, message: str) -> None:
        self.database.save_log("ERROR", message)
