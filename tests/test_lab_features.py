"""
tests/test_lab_features.py
--------------------------
Pruebas de las funciones de laboratorio V1.3: continuidad de audio
(dropouts), espectro, scoring, comparacion y perfilado de firmware.
"""

from __future__ import annotations

import tempfile
import unittest
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from audio.audio_test import make_tone
from audio.dropout_test import SAMPLE_RATE, analyze_continuity
from audio.spectrum import compute_spectrogram, compute_spectrum
from ble.firmware_profiler import profile_firmware
from core.comparison import compare_devices, compare_over_time
from core.scoring import (
    continuity_score,
    device_quality_score,
    jitter_score,
    latency_score,
    rms_balance_score,
)
from database.database import DatabaseManager


@dataclass
class FakeDevice:
    mac: str
    name: str
    rssi: int
    manufacturer: str = "Test"
    uuids: list = field(default_factory=list)


class DropoutAnalysisTest(unittest.TestCase):
    def _tone_with_gaps(self, gaps: list[tuple[float, float]]) -> np.ndarray:
        """Tono de 2 s con huecos (inicio_s, duracion_s) insertados."""
        signal = 0.5 * make_tone(1000.0, 2.0).astype(np.float64)
        for start_s, dur_s in gaps:
            a = int(start_s * SAMPLE_RATE)
            b = a + int(dur_s * SAMPLE_RATE)
            signal[a:b] = 0.0
        return signal

    def test_clean_tone_full_continuity(self) -> None:
        result = analyze_continuity(self._tone_with_gaps([]))
        self.assertIsNotNone(result)
        self.assertEqual(result.dropouts, 0)
        self.assertGreater(result.continuity_pct, 99.0)

    def test_gaps_are_detected(self) -> None:
        # Dos huecos de 50 ms y 80 ms dentro del tono.
        result = analyze_continuity(
            self._tone_with_gaps([(0.5, 0.05), (1.2, 0.08)])
        )
        self.assertIsNotNone(result)
        self.assertEqual(result.dropouts, 2)
        self.assertAlmostEqual(result.total_gap_ms, 130.0, delta=25.0)
        self.assertLess(result.continuity_pct, 99.0)

    def test_silence_returns_none(self) -> None:
        self.assertIsNone(analyze_continuity(np.zeros(SAMPLE_RATE)))


class SpectrumTest(unittest.TestCase):
    def test_spectrum_peak_at_tone_frequency(self) -> None:
        tone = make_tone(1000.0, 1.0)
        freqs, mag_db = compute_spectrum(tone)
        peak_freq = freqs[int(np.argmax(mag_db))]
        self.assertAlmostEqual(peak_freq, 1000.0, delta=5.0)

    def test_spectrogram_shapes(self) -> None:
        tone = make_tone(440.0, 1.0)
        f, t, sxx = compute_spectrogram(tone)
        self.assertEqual(sxx.shape, (len(f), len(t)))
        self.assertGreater(len(t), 10)


class ScoringTest(unittest.TestCase):
    def test_score_formulas(self) -> None:
        self.assertEqual(latency_score(50.0), 100.0)
        self.assertLess(latency_score(300.0), 40.0)
        self.assertEqual(jitter_score(0.0), 100.0)
        self.assertEqual(rms_balance_score(0.0), 100.0)
        self.assertLess(rms_balance_score(5.0), 50.0)
        self.assertEqual(continuity_score(100.0), 100.0)
        self.assertLess(continuity_score(90.0), 85.0)

    def test_device_quality_score_with_history(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            mac = "AA:BB:CC:DD:EE:01"
            for rssi in (-50, -51, -50, -49):
                db.save_scan_results([FakeDevice(mac, "TWS", rssi)])
            for level in (100, 98, 100, 97, 99, 100, 98, 100):
                db.save_battery(mac, level)
            db.save_latency(90.0, jitter_ms=3.0, classification="Buena")

            result = device_quality_score(
                db, mac, extras={"rms_diff_db": 0.5, "continuity_pct": 99.5}
            )
            db.close()

            self.assertIn("estabilidad", result["scores"])
            self.assertIn("bateria", result["scores"])
            self.assertIn("latencia", result["scores"])
            self.assertIn("audio", result["scores"])
            self.assertEqual(result["missing"], [])
            self.assertGreater(result["overall"], 80.0)

    def test_missing_metrics_redistributed(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            result = device_quality_score(db, "ZZ:ZZ:ZZ:ZZ:ZZ:ZZ")
            db.close()
            # Solo estabilidad disponible: el resto reportado como faltante.
            self.assertIn("bateria", result["missing"])
            self.assertIn("latencia", result["missing"])


class ComparisonTest(unittest.TestCase):
    def test_compare_devices_winner(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            good, bad = "AA:00:00:00:00:01", "AA:00:00:00:00:02"
            for rssi in (-50, -50, -51, -50):
                db.save_scan_results([FakeDevice(good, "Bueno", rssi)])
            for rssi in (-40, -90, -45, -88):
                db.save_scan_results([FakeDevice(bad, "Malo", rssi)])
            for _ in range(4):
                db.save_ble_event(bad, "disconnected", "test")

            result = compare_devices(db, good, bad)
            db.close()
            self.assertEqual(result["winner"], good)
            self.assertGreater(result["deltas"]["estabilidad"], 0)

    def test_compare_over_time_detects_degradation(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            db = DatabaseManager(Path(tmp) / "test.db")
            mac = "AA:00:00:00:00:03"
            for level in (100, 99, 100, 98, 88, 85, 87, 86):
                db.save_battery(mac, level)
            result = compare_over_time(db, mac)
            db.close()
            self.assertIsNotNone(result)
            self.assertTrue(result["degrading"])
            self.assertLess(result["peak_trend_pct"], 0)


class FirmwareProfilerTest(unittest.TestCase):
    def test_qualcomm_chipset(self) -> None:
        profile = profile_firmware("Qualcomm", [])
        self.assertIn("QCC", profile["probable_chipset"])
        self.assertTrue(profile["speculative"])

    def test_fast_pair_ecosystem(self) -> None:
        profile = profile_firmware(
            "Desconocido", ["0000fe2c-0000-1000-8000-00805f9b34fb"]
        )
        self.assertEqual(profile["ecosystem"], "Android (Fast Pair)")

    def test_proprietary_services_counted(self) -> None:
        profile = profile_firmware(
            "Sony",
            ["91c10d9c-aaaa-bbbb-cccc-1234567890ab"],
            services=[{"uuid": "0000180f-0000-1000-8000-00805f9b34fb"}],
        )
        self.assertEqual(len(profile["proprietary_services"]), 1)
        self.assertEqual(profile["ecosystem"], "generico")


if __name__ == "__main__":
    unittest.main()
