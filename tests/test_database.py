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

    def test_power_and_latency_history(self) -> None:
        self.db.save_power(85.0, True)
        self.db.save_power(None, True)  # sin sensor: debe ignorarse
        self.db.save_latency(
            120.5, jitter_ms=4.2, confidence=0.9,
            channel="both", classification="Buena",
        )
        columns, rows = self.db._dump_table("power_history")
        self.assertEqual(len(rows), 1)
        self.assertIn("percent", columns)
        _, lat_rows = self.db._dump_table("latency_history")
        self.assertEqual(len(lat_rows), 1)

    def test_export_json_and_csv(self) -> None:
        self.db.save_battery("AA:BB:CC:DD:EE:01", 70)
        out_dir = Path(self._tmp.name) / "exports"

        json_path = self.db.export_json(out_dir)
        self.assertIsNotNone(json_path)
        import json

        with open(json_path, encoding="utf-8") as fh:
            payload = json.load(fh)
        self.assertEqual(len(payload["tables"]["battery_history"]), 1)

        csv_paths = self.db.export_csv(out_dir)
        self.assertEqual(len(csv_paths), 6)  # una por tabla


class ConfigTest(unittest.TestCase):
    def test_defaults_when_file_missing(self) -> None:
        from core.config import Config

        cfg = Config.load(Path("/ruta/inexistente/config.json"))
        self.assertEqual(cfg.get("scan.auto_refresh_ms"), 5000)
        self.assertEqual(cfg.get("clave.inexistente", "x"), "x")


if __name__ == "__main__":
    unittest.main()
