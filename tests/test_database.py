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
        from database.database import EXPORT_TABLES

        self.assertEqual(len(csv_paths), len(EXPORT_TABLES))  # una por tabla

    def test_fingerprint_roundtrip(self) -> None:
        fingerprint = {
            "mac": "AA:BB:CC:DD:EE:09",
            "name": "TWS-Test",
            "manufacturer_data": {"0x004C": "0215aabb"},
            "uuids": ["0000180f-0000-1000-8000-00805f9b34fb"],
            "services": [{"uuid": "180f", "name": "Battery Service"}],
            "mtu": 247,
            "avg_rssi": -55.5,
            "codecs": ["SBC", "AAC"],
        }
        self.db.save_fingerprint(fingerprint)
        stored = self.db.get_fingerprint("AA:BB:CC:DD:EE:09")
        self.assertIsNotNone(stored)
        self.assertEqual(stored["mtu"], 247)
        self.assertEqual(stored["codecs"], ["SBC", "AAC"])
        self.assertEqual(stored["uuids"], fingerprint["uuids"])

        # Upsert: actualizar no debe duplicar.
        fingerprint["mtu"] = 185
        self.db.save_fingerprint(fingerprint)
        self.assertEqual(self.db.get_fingerprint("AA:BB:CC:DD:EE:09")["mtu"], 185)

    def test_ble_events_and_charge(self) -> None:
        self.db.save_ble_event("AA:BB:CC:DD:EE:01", "disconnected", "test")
        self.db.save_charge_sample("UM25C", 5.12, 0.45, 2.3, capacity_mah=120.0)
        _, events = self.db._dump_table("ble_events")
        _, charges = self.db._dump_table("charge_history")
        self.assertEqual(len(events), 1)
        self.assertEqual(len(charges), 1)


class ConfigTest(unittest.TestCase):
    def test_defaults_when_file_missing(self) -> None:
        from core.config_manager import ConfigManager

        cfg = ConfigManager.load(Path("/ruta/inexistente/config.json"))
        self.assertEqual(cfg.get("scan.auto_refresh_ms"), 5000)
        self.assertEqual(cfg.get("clave.inexistente", "x"), "x")


if __name__ == "__main__":
    unittest.main()
