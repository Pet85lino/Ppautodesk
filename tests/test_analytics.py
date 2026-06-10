"""
tests/test_analytics.py
-----------------------
Pruebas de la analitica historica (salud de bateria, estabilidad)
y de la deteccion de codecs.
"""

from __future__ import annotations

import tempfile
import unittest
from dataclasses import dataclass, field
from pathlib import Path

from ble.gatt_parser import infer_codecs
from core import analytics
from database.database import DatabaseManager


@dataclass
class FakeDevice:
    mac: str
    name: str
    rssi: int
    manufacturer: str = "Test"
    uuids: list = field(default_factory=list)


class BatteryHealthTest(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.db = DatabaseManager(Path(self._tmp.name) / "test.db")
        self.mac = "AA:BB:CC:DD:EE:01"

    def tearDown(self) -> None:
        self.db.close()
        self._tmp.cleanup()

    def test_insufficient_data_returns_none(self) -> None:
        self.db.save_battery(self.mac, 90)
        self.assertIsNone(analytics.battery_health_score(self.db, self.mac))

    def test_degraded_battery_detected(self) -> None:
        # Historia: antes llegaba a 100 %, recientemente solo a 80 %.
        for level in (100, 98, 100, 95, 90, 85, 78, 80):
            self.db.save_battery(self.mac, level)
        result = analytics.battery_health_score(self.db, self.mac)
        self.assertIsNotNone(result)
        self.assertEqual(result["historical_peak"], 100)
        self.assertEqual(result["recent_peak"], 80)
        self.assertAlmostEqual(result["health_score"], 0.8, places=2)

    def test_healthy_battery_score_one(self) -> None:
        for level in (95, 100, 97, 100, 99, 100, 98, 100):
            self.db.save_battery(self.mac, level)
        result = analytics.battery_health_score(self.db, self.mac)
        self.assertEqual(result["health_score"], 1.0)


class StabilityScoreTest(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.db = DatabaseManager(Path(self._tmp.name) / "test.db")
        self.mac = "AA:BB:CC:DD:EE:02"

    def tearDown(self) -> None:
        self.db.close()
        self._tmp.cleanup()

    def _scan(self, rssi: int) -> None:
        self.db.save_scan_results([FakeDevice(self.mac, "TWS", rssi)])

    def test_stable_link_high_score(self) -> None:
        for rssi in (-50, -51, -50, -49, -50, -50):
            self._scan(rssi)
        result = analytics.stability_score(self.db, self.mac)
        self.assertGreater(result["score"], 85)

    def test_disconnects_lower_score(self) -> None:
        for rssi in (-50, -51, -50, -49):
            self._scan(rssi)
        baseline = analytics.stability_score(self.db, self.mac)["score"]
        for _ in range(3):
            self.db.save_ble_event(self.mac, "disconnected", "test")
        degraded = analytics.stability_score(self.db, self.mac)["score"]
        self.assertLess(degraded, baseline)

    def test_rssi_variance_lowers_score(self) -> None:
        for rssi in (-40, -80, -45, -90, -50, -85):
            self._scan(rssi)
        result = analytics.stability_score(self.db, self.mac)
        self.assertLess(result["components"]["rssi"], 0.5)


class CodecInferenceTest(unittest.TestCase):
    def test_sbc_always_present(self) -> None:
        self.assertIn("SBC", infer_codecs("Desconocido", []))

    def test_sony_implies_ldac(self) -> None:
        codecs = infer_codecs("Sony", [])
        self.assertIn("LDAC", codecs)
        self.assertIn("AAC", codecs)

    def test_qualcomm_implies_aptx(self) -> None:
        self.assertIn("aptX", infer_codecs("Qualcomm", []))

    def test_fast_pair_adds_android_codecs(self) -> None:
        codecs = infer_codecs(
            "Desconocido", ["0000fe2c-0000-1000-8000-00805f9b34fb"]
        )
        self.assertIn("AAC", codecs)
        self.assertIn("aptX", codecs)


if __name__ == "__main__":
    unittest.main()
