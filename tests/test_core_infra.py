"""
tests/test_core_infra.py
------------------------
Pruebas del bus de eventos, el estado central y el generador de reportes.
"""

from __future__ import annotations

import tempfile
import unittest
from dataclasses import dataclass, field
from pathlib import Path

from PySide6.QtCore import QCoreApplication

from core.app_state import AppState
from core.event_bus import EventBus
from core.report_generator import generate_report
from database.database import DatabaseManager

# Las senales Qt requieren una aplicacion (basta QCoreApplication, sin GUI).
_app = QCoreApplication.instance() or QCoreApplication([])


@dataclass
class FakeDevice:
    mac: str
    name: str
    rssi: int
    manufacturer: str = "Test"
    uuids: list = field(default_factory=list)


class EventBusTest(unittest.TestCase):
    def test_publish_subscribe(self) -> None:
        bus = EventBus()
        received = []
        bus.subscribe("ble.scan", received.append)
        bus.publish("ble.scan", [1, 2, 3])
        self.assertEqual(received, [[1, 2, 3]])

    def test_unsubscribe(self) -> None:
        bus = EventBus()
        received = []
        bus.subscribe("x", received.append)
        bus.unsubscribe("x", received.append)
        bus.publish("x", 1)
        self.assertEqual(received, [])

    def test_broken_subscriber_does_not_block_others(self) -> None:
        bus = EventBus()
        received = []

        def broken(_payload):
            raise RuntimeError("suscriptor roto")

        bus.subscribe("x", broken)
        bus.subscribe("x", received.append)
        bus.publish("x", 42)
        self.assertEqual(received, [42])


class AppStateTest(unittest.TestCase):
    def test_scan_feeds_rssi_history(self) -> None:
        state = AppState()
        dev = FakeDevice("AA:BB:CC:DD:EE:01", "TWS", -50)
        state.apply_scan([dev])
        state.apply_scan([FakeDevice("AA:BB:CC:DD:EE:01", "TWS", -55)])
        self.assertEqual(list(state.rssi_history["AA:BB:CC:DD:EE:01"]), [-50, -55])
        self.assertAlmostEqual(state.avg_rssi("AA:BB:CC:DD:EE:01"), -52.5)

    def test_battery_cache_and_live_series(self) -> None:
        state = AppState()
        state.set_battery("AA", 80)
        state.set_battery("AA", None)  # protocolo propietario: no rompe
        state.set_battery("AA", 78)
        self.assertEqual(state.battery_levels["AA"], 78)
        self.assertEqual(list(state.battery_history_live["AA"]), [80, 78])

    def test_device_lookup(self) -> None:
        state = AppState()
        dev = FakeDevice("AA", "TWS", -40)
        state.apply_scan([dev])
        self.assertIs(state.device_by_mac("AA"), dev)
        self.assertIsNone(state.device_by_mac("ZZ"))


class ReportGeneratorTest(unittest.TestCase):
    def test_report_is_generated(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            mac = "AA:BB:CC:DD:EE:01"
            for level in (100, 95, 90, 88):
                db.save_battery(mac, level)

            report = {
                "device": {"name": "TWS-Test", "mac": mac,
                           "manufacturer": "Sony", "rssi": -50},
                "battery": 88,
                "latency": {"latency_ms": 120.0, "confidence": 0.9,
                            "classification": "Buena"},
                "jitter": {"mean_ms": 118.0, "std_ms": 3.0},
                "rms": {"diff_db": 0.5},
                "analytics": {
                    "battery_health": None,
                    "stability": {"score": 90.0, "components": {}},
                    "rssi": None,
                    "capabilities": None,
                },
            }
            path = generate_report(report, db, Path(tmp) / "exports")
            db.close()
            self.assertIsNotNone(path)
            self.assertTrue(path.exists())
            self.assertGreater(path.stat().st_size, 0)


if __name__ == "__main__":
    unittest.main()
