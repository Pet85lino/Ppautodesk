"""
tests/test_session_charge.py
----------------------------
Pruebas del grabador de sesiones y del grabador de curvas de carga
(con lector inyectado: sin hardware).
"""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PySide6.QtCore import QCoreApplication, Qt

from core.session_recorder import SessionRecorder
from database.database import DatabaseManager
from usb.charge_recorder import ChargeRecorder
from usb.usb_meter import ChargeSample

_app = QCoreApplication.instance() or QCoreApplication([])


class SessionRecorderTest(unittest.TestCase):
    def test_session_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            mac = "AA:BB:CC:DD:EE:01"
            db.save_ble_event(mac, "disconnected", "test")

            session = SessionRecorder(mac, base_dir=Path(tmp) / "sessions")
            json_path = session.save_session_json({"device": {"mac": mac}})
            ble_path = session.save_ble_log(db, mac)
            wav_path = session.save_waveform(
                np.sin(np.linspace(0, 100, 4800)).astype(np.float32)
            )
            db.close()

            self.assertTrue(json_path.exists())
            self.assertTrue(ble_path.exists())
            self.assertTrue(wav_path.exists())
            self.assertTrue(session.plots_dir.is_dir())

            payload = json.loads(ble_path.read_text(encoding="utf-8"))
            self.assertEqual(len(payload["events"]), 1)
            self.assertEqual(payload["events"][0]["type"], "disconnected")

    def test_empty_waveform_is_skipped(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            session = SessionRecorder("AA:BB", base_dir=Path(tmp))
            self.assertIsNone(session.save_waveform(None))
            self.assertIsNone(session.save_waveform([]))


class ChargeRecorderTest(unittest.TestCase):
    def test_recorder_emits_samples_and_overheat(self) -> None:
        samples_seen: list = []
        overheats: list = []

        def fake_reader(_port: str, _model: str) -> ChargeSample:
            # Tercera muestra caliente para disparar la alerta.
            temp = 50.0 if len(samples_seen) >= 2 else 30.0
            return ChargeSample(
                source="UM25C", voltage_v=5.1, current_a=0.4,
                power_w=2.04, temp_c=temp,
            )

        recorder = ChargeRecorder("FAKE", "UM25C", interval_s=0.5, reader=fake_reader)
        # Conexion directa: sin event loop corriendo en el test, las
        # conexiones queued nunca se despacharian.
        recorder.sample_ready.connect(samples_seen.append, Qt.ConnectionType.DirectConnection)
        recorder.overheat.connect(overheats.append, Qt.ConnectionType.DirectConnection)
        recorder.start()
        import time

        time.sleep(1.8)
        recorder.stop()

        self.assertGreaterEqual(len(samples_seen), 3)
        self.assertGreaterEqual(len(overheats), 1)
        self.assertEqual(samples_seen[0].voltage_v, 5.1)

    def test_recorder_stops_after_failures(self) -> None:
        def dead_reader(_port: str, _model: str):
            return None

        errors: list = []
        recorder = ChargeRecorder("FAKE", "UM25C", interval_s=0.5, reader=dead_reader)
        recorder.recorder_error.connect(errors.append, Qt.ConnectionType.DirectConnection)
        recorder.start()
        import time

        time.sleep(2.5)
        recorder.stop()
        self.assertEqual(len(errors), 1)
        self.assertIn("no responde", errors[0])


if __name__ == "__main__":
    unittest.main()
