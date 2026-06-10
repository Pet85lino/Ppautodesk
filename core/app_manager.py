"""
core/app_manager.py
-------------------
Orquestador central de la aplicacion.

Responsabilidades:
    * Cargar configuracion e inicializar logging.
    * Crear la base de datos y el motor BLE.
    * Conectar las senales del motor a la persistencia (toda escritura
      SQLite ocurre en el hilo principal de Qt, via senales).
    * Apagado ordenado de todos los subsistemas.

La UI (dashboard) recibe una instancia de AppManager y nunca crea
recursos por su cuenta: separacion estricta UI / backend.
"""

from __future__ import annotations

import logging

from ble.ble_scanner import BLEEngine
from core.config import Config
from core.logger import setup_logging
from database.database import DatabaseManager

logger = logging.getLogger("lino.core.app")


class AppManager:
    """Punto unico de acceso a los subsistemas de la suite."""

    def __init__(self):
        # 1. Configuracion + logging (antes que cualquier otro subsistema).
        self.config = Config.load()
        setup_logging(self.config.get("logging.level", "INFO"))
        logger.info(
            "%s v%s inicializando...",
            self.config.get("app_name"),
            self.config.get("version"),
        )

        # 2. Persistencia SQLite.
        self.database = DatabaseManager(self.config.database_path)

        # 3. Motor BLE en hilo dedicado.
        self.ble_engine = BLEEngine(
            scan_duration=float(self.config.get("scan.duration_seconds", 4.0)),
            connect_timeout=float(
                self.config.get("battery.connect_timeout_seconds", 10.0)
            ),
        )

        # 4. Persistencia automatica de resultados BLE.
        self.ble_engine.scan_finished.connect(self._on_scan_finished)
        self.ble_engine.battery_read.connect(self._on_battery_read)
        self.ble_engine.engine_error.connect(self._on_engine_error)

    # ------------------------------------------------------------------
    # Ciclo de vida
    # ------------------------------------------------------------------
    def start(self) -> None:
        """Arranca los subsistemas en segundo plano."""
        self.ble_engine.start()
        self.database.save_log("INFO", "Aplicacion iniciada")

    def shutdown(self) -> None:
        """Apagado ordenado: motor BLE primero, base de datos al final."""
        logger.info("Apagando subsistemas...")
        self.database.save_log("INFO", "Aplicacion cerrada")
        self.ble_engine.shutdown()
        self.database.close()

    # ------------------------------------------------------------------
    # Persistencia de eventos BLE (ejecutado en el hilo principal de Qt)
    # ------------------------------------------------------------------
    def _on_scan_finished(self, devices: list) -> None:
        self.database.save_scan_results(devices)

    def _on_battery_read(self, mac: str, level) -> None:
        self.database.save_battery(mac, level)
        if level is not None:
            self.database.save_log("INFO", f"Bateria {mac}: {level}%")

    def _on_engine_error(self, message: str) -> None:
        self.database.save_log("ERROR", message)
