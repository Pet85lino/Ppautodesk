"""
tests/test_robustness.py
------------------------
Pruebas V1.4: thread-safety de la base de datos, backoff BLE,
versionado de reportes, perfilado de flota y monitor de rendimiento.
"""

from __future__ import annotations

import tempfile
import threading
import unittest
from dataclasses import dataclass, field
from pathlib import Path

from PySide6.QtCore import QCoreApplication

from ble.ble_scanner import backoff_delays
from core.perf_monitor import PerfMonitor
from core.profiling_db import fleet_profile, manufacturer_patterns, uuid_clusters
from core.versioning import engine_metadata
from database.database import DatabaseManager

_app = QCoreApplication.instance() or QCoreApplication([])


@dataclass
class FakeDevice:
    mac: str
    name: str
    rssi: int
    manufacturer: str = "Test"
    uuids: list = field(default_factory=list)


class BackoffTest(unittest.TestCase):
    def test_exponential_growth(self) -> None:
        self.assertEqual(backoff_delays(3, base_s=1.0), [1.0, 2.0, 4.0])

    def test_cap_applies(self) -> None:
        delays = backoff_delays(8, base_s=1.0, cap_s=30.0)
        self.assertEqual(max(delays), 30.0)

    def test_zero_attempts(self) -> None:
        self.assertEqual(backoff_delays(0), [])


class ThreadSafetyTest(unittest.TestCase):
    def test_concurrent_writes_do_not_corrupt(self) -> None:
        """Escrituras desde 8 hilos simultaneos: el lock + WAL deben
        garantizar que no se pierda ni corrompa ninguna fila."""
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            writes_per_thread = 25
            threads = 8

            def writer(thread_id: int) -> None:
                mac = f"AA:00:00:00:00:{thread_id:02X}"
                for i in range(writes_per_thread):
                    db.save_battery(mac, 50 + (i % 50))
                    db.save_ble_event(mac, "rssi_jump", f"escritura {i}")
                    db.save_scan_results([FakeDevice(mac, f"T{thread_id}", -50 - i)])

            workers = [
                threading.Thread(target=writer, args=(t,)) for t in range(threads)
            ]
            for w in workers:
                w.start()
            for w in workers:
                w.join()

            total = threads * writes_per_thread
            _, battery_rows = db._dump_table("battery_history")
            _, event_rows = db._dump_table("ble_events")
            _, scan_rows = db._dump_table("scan_history")
            self.assertEqual(len(battery_rows), total)
            self.assertEqual(len(event_rows), total)
            self.assertEqual(len(scan_rows), total)
            self.assertEqual(db.known_devices_count(), threads)
            db.close()

    def test_query_api_concurrent_with_writes(self) -> None:
        """Lecturas via API mientras otro hilo escribe: sin excepciones."""
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            mac = "AA:00:00:00:00:01"
            stop = threading.Event()
            errors: list[Exception] = []

            def writer() -> None:
                i = 0
                while not stop.is_set():
                    db.save_scan_results([FakeDevice(mac, "TWS", -50 - (i % 10))])
                    i += 1

            def reader() -> None:
                try:
                    for _ in range(200):
                        db.rssi_values(mac)
                        db.scan_count(mac)
                        db.devices_catalog()
                except Exception as exc:  # noqa: BLE001
                    errors.append(exc)

            w = threading.Thread(target=writer)
            r = threading.Thread(target=reader)
            w.start()
            r.start()
            r.join()
            stop.set()
            w.join()
            db.close()
            self.assertEqual(errors, [])


class VersioningTest(unittest.TestCase):
    def test_metadata_block(self) -> None:
        meta = engine_metadata("0.5.0")
        self.assertIn("schema_version", meta)
        self.assertIn("scoring_version", meta)
        self.assertIn("analytics_version", meta)
        self.assertEqual(meta["analysis_engine"], "lino-audio-diagnostic/0.5.0")


class ProfilingDbTest(unittest.TestCase):
    def _seed(self, db: DatabaseManager) -> None:
        db.save_fingerprint({
            "mac": "AA:00:00:00:00:01",
            "name": "TWS-Sony",
            "manufacturer_data": {"0x012D": "0102"},
            "uuids": ["0000180f-0000-1000-8000-00805f9b34fb",
                      "91c10d9c-aaaa-bbbb-cccc-1234567890ab"],
            "services": [],
            "mtu": 247,
            "codecs": ["SBC", "AAC", "LDAC"],
        })
        db.save_fingerprint({
            "mac": "AA:00:00:00:00:02",
            "name": "TWS-Sony-2",
            "manufacturer_data": {"0x012D": "0304"},
            "uuids": ["0000180f-0000-1000-8000-00805f9b34fb"],
            "services": [],
            "mtu": 185,
            "codecs": ["SBC", "AAC"],
        })

    def test_uuid_clusters(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            self._seed(db)
            clusters = uuid_clusters(db)
            db.close()
            self.assertEqual(clusters["total_devices"], 2)
            # Battery Service compartido por ambos.
            self.assertEqual(
                clusters["sig_uuids"]["0000180f-0000-1000-8000-00805f9b34fb"], 2
            )
            self.assertEqual(len(clusters["proprietary_uuids"]), 1)

    def test_manufacturer_patterns(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            self._seed(db)
            patterns = manufacturer_patterns(db)
            db.close()
            sony = patterns["0x012D"]
            self.assertEqual(sony["devices"], 2)
            self.assertIn("SBC", sony["common_codecs"])
            self.assertEqual(sony["typical_mtu"], 216)  # media de 247 y 185

    def test_fleet_profile_shape(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            profile = fleet_profile(db)
            db.close()
            self.assertIn("uuid_clusters", profile)
            self.assertIn("manufacturer_patterns", profile)


class PerfMonitorTest(unittest.TestCase):
    def test_sample_and_summary(self) -> None:
        monitor = PerfMonitor(interval_s=60.0)
        monitor.start()
        sample = monitor.sample()
        monitor.stop()

        self.assertGreater(sample["rss_mb"], 0)
        self.assertIsInstance(sample["top_allocations"], list)
        summary = monitor.summary()
        self.assertEqual(summary["samples"], 1)
        self.assertGreater(summary["rss_max_mb"], 0)


if __name__ == "__main__":
    unittest.main()
