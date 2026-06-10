"""
tests/test_database.py
----------------------
Pruebas unitarias de la capa de persistencia SQLite.

Ejecucion:
    python -m unittest discover tests
"""

from __future__ import annotations

import tempfile
import unittest
from dataclasses import dataclass
from pathlib import Path

from database.database import DatabaseManager


@dataclass
class FakeDevice:
    """Doble de DeviceInfo sin dependencias de bleak/Qt."""

    mac: str
    name: str
    rssi: int
    manufacturer: str = "Test"


class DatabaseManagerTest(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.db = DatabaseManager(Path(self._tmp.name) / "test.db")

    def tearDown(self) -> None:
        self.db.close()
        self._tmp.cleanup()

    def test_save_scan_updates_catalog(self) -> None:
        devices = [
            FakeDevice("AA:BB:CC:DD:EE:01", "TWS-L", -50),
            FakeDevice("AA:BB:CC:DD:EE:02", "TWS-R", -60),
        ]
        self.db.save_scan_results(devices)
        self.assertEqual(self.db.known_devices_count(), 2)

        # Un segundo escaneo del mismo dispositivo no duplica el catalogo.
        self.db.save_scan_results(devices[:1])
        self.assertEqual(self.db.known_devices_count(), 2)

    def test_battery_history_roundtrip(self) -> None:
        mac = "AA:BB:CC:DD:EE:01"
        self.db.save_battery(mac, 80)
        self.db.save_battery(mac, 75)
        history = self.db.battery_history(mac)
        self.assertEqual([level for _, level in history], [80, 75])

    def test_battery_none_is_ignored(self) -> None:
        # Dispositivos sin Battery Service no deben ensuciar el historial.
        self.db.save_battery("AA:BB:CC:DD:EE:01", None)
        self.assertEqual(self.db.battery_history("AA:BB:CC:DD:EE:01"), [])

    def test_save_log(self) -> None:
        # No debe lanzar excepcion; verificacion minima de persistencia.
        self.db.save_log("INFO", "evento de prueba")


class ConfigTest(unittest.TestCase):
    def test_defaults_when_file_missing(self) -> None:
        from core.config import Config

        cfg = Config.load(Path("/ruta/inexistente/config.json"))
        self.assertEqual(cfg.get("scan.auto_refresh_ms"), 5000)
        self.assertEqual(cfg.get("clave.inexistente", "x"), "x")


if __name__ == "__main__":
    unittest.main()
