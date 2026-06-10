"""
tests/test_audio_dsp.py
-----------------------
Pruebas de los generadores de senal y del pipeline DSP de latencia.

No requieren hardware de audio: validan las funciones puras (numpy/scipy)
que alimentan a sounddevice en tiempo de ejecucion.
"""

from __future__ import annotations

import unittest

import numpy as np

from audio.audio_test import (
    MAX_SAFE_VOLUME,
    MAX_TEST_DURATION,
    SAMPLE_RATE,
    _clamp_duration,
    _to_stereo,
    make_pink_noise,
    make_sweep,
    make_tone,
    make_white_noise,
)
from audio.latency_test import (
    analyze_recording,
    classify_latency,
    make_chirp,
)


class SignalGeneratorTest(unittest.TestCase):
    def test_tone_length_and_range(self) -> None:
        tone = make_tone(440.0, 0.5)
        self.assertEqual(len(tone), SAMPLE_RATE // 2)
        self.assertLessEqual(float(np.max(np.abs(tone))), 1.0)

    def test_noise_normalized(self) -> None:
        for noise in (make_white_noise(0.2, seed=1), make_pink_noise(0.2, seed=1)):
            self.assertAlmostEqual(float(np.max(np.abs(noise))), 1.0, places=5)

    def test_pink_noise_has_no_dc(self) -> None:
        pink = make_pink_noise(0.5, seed=42)
        spectrum = np.abs(np.fft.rfft(pink))
        self.assertAlmostEqual(float(spectrum[0]), 0.0, places=3)

    def test_sweep_covers_duration(self) -> None:
        sweep = make_sweep(duration=2.0)
        self.assertEqual(len(sweep), 2 * SAMPLE_RATE)


class HearingProtectionTest(unittest.TestCase):
    def test_volume_is_clamped(self) -> None:
        # Pedir volumen 1.0 nunca debe superar el limite de seguridad.
        stereo = _to_stereo(make_tone(440.0, 0.1), "both", volume=1.0)
        self.assertLessEqual(float(np.max(np.abs(stereo))), MAX_SAFE_VOLUME + 1e-6)

    def test_duration_is_clamped(self) -> None:
        self.assertEqual(_clamp_duration(999.0), MAX_TEST_DURATION)
        self.assertGreater(_clamp_duration(0.0), 0.0)

    def test_channel_isolation(self) -> None:
        # El canal no seleccionado debe quedar en silencio absoluto.
        stereo = _to_stereo(make_tone(440.0, 0.1), "left", volume=0.4)
        self.assertEqual(float(np.max(np.abs(stereo[:, 1]))), 0.0)
        self.assertGreater(float(np.max(np.abs(stereo[:, 0]))), 0.0)


class LatencyDspTest(unittest.TestCase):
    def test_known_delay_is_recovered(self) -> None:
        # Grabacion sintetica: chirp insertado con retardo conocido.
        chirp = make_chirp()
        delay_ms = 120.0
        delay_samples = int(delay_ms * SAMPLE_RATE / 1000)
        recorded = np.zeros(delay_samples + len(chirp) + SAMPLE_RATE // 2)
        recorded[delay_samples: delay_samples + len(chirp)] = chirp

        measured_ms, confidence, _ = analyze_recording(recorded, chirp)
        self.assertAlmostEqual(measured_ms, delay_ms, delta=1.0)
        self.assertGreater(confidence, 0.5)  # pico limpio = confianza alta

    def test_noise_only_gives_low_confidence(self) -> None:
        rng = np.random.default_rng(7)
        recorded = rng.standard_normal(SAMPLE_RATE)
        _, confidence, _ = analyze_recording(recorded, make_chirp())
        self.assertLess(confidence, 0.5)

    def test_classification_thresholds(self) -> None:
        self.assertEqual(classify_latency(50), "Excelente")
        self.assertEqual(classify_latency(100), "Buena")
        self.assertEqual(classify_latency(200), "Normal")
        self.assertEqual(classify_latency(280), "Elevada")
        self.assertEqual(classify_latency(350), "Alta")


if __name__ == "__main__":
    unittest.main()
