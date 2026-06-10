"""
tests/test_mic_profile.py
-------------------------
Pruebas del perfilador de microfono (analisis puro, sin hardware).
"""

from __future__ import annotations

import unittest

import numpy as np

from audio.mic_profile import (
    SAMPLE_RATE,
    MicProfile,
    analyze_enc,
    chipset_mic_hint,
    format_mic_profile,
    interpret_topology,
    sensitivity_dbfs,
    speech_clarity_score,
)


def _noise(duration_s: float, amplitude: float, seed: int = 1) -> np.ndarray:
    rng = np.random.default_rng(seed)
    return amplitude * rng.standard_normal(int(SAMPLE_RATE * duration_s))


class TopologyTest(unittest.TestCase):
    def test_channel_interpretation(self) -> None:
        self.assertIn("mono", interpret_topology(1))
        self.assertIn("dual", interpret_topology(2))
        self.assertIn("array", interpret_topology(4))
        self.assertIn("desconocida", interpret_topology(None))
        self.assertIn("desconocida", interpret_topology(0))


class EncAnalysisTest(unittest.TestCase):
    def test_adaptive_suppression_detected(self) -> None:
        # Ruido que cae 12 dB tras el primer segundo: ENC convergiendo.
        head = _noise(1.0, amplitude=0.20)
        tail = _noise(3.0, amplitude=0.05, seed=2)  # -12 dB
        result = analyze_enc(np.concatenate([head, tail]))
        self.assertIsNotNone(result)
        self.assertEqual(result["status"], "Detected")
        self.assertGreater(result["reduction_db"], 6.0)

    def test_constant_noise_not_detected(self) -> None:
        result = analyze_enc(_noise(4.0, amplitude=0.1))
        self.assertIsNotNone(result)
        self.assertEqual(result["status"], "Not detected")
        self.assertLess(abs(result["reduction_db"]), 3.0)

    def test_short_recording_returns_none(self) -> None:
        self.assertIsNone(analyze_enc(_noise(1.0, amplitude=0.1)))

    def test_silence_returns_none(self) -> None:
        self.assertIsNone(analyze_enc(np.zeros(4 * SAMPLE_RATE)))


class SensitivityTest(unittest.TestCase):
    def test_known_amplitude(self) -> None:
        # Seno de amplitud 0.1 -> RMS 0.0707 -> ~-23 dBFS.
        t = np.linspace(0, 1.0, SAMPLE_RATE, endpoint=False)
        tone = 0.1 * np.sin(2 * np.pi * 440 * t)
        self.assertAlmostEqual(sensitivity_dbfs(tone), -23.0, delta=0.5)

    def test_silence_returns_none(self) -> None:
        self.assertIsNone(sensitivity_dbfs(np.zeros(SAMPLE_RATE)))


class SpeechClarityTest(unittest.TestCase):
    def test_speech_band_tone_scores_high(self) -> None:
        # Tono de 1 kHz: toda la energia dentro de la banda de voz.
        t = np.linspace(0, 1.0, SAMPLE_RATE, endpoint=False)
        tone = np.sin(2 * np.pi * 1000 * t)
        self.assertGreater(speech_clarity_score(tone), 95.0)

    def test_out_of_band_tone_scores_low(self) -> None:
        t = np.linspace(0, 1.0, SAMPLE_RATE, endpoint=False)
        tone = np.sin(2 * np.pi * 10_000 * t)  # fuera de 300-3400 Hz
        self.assertLess(speech_clarity_score(tone), 5.0)

    def test_white_noise_scores_proportional(self) -> None:
        # Ruido blanco: la banda de voz es ~13 % del espectro a 48 kHz.
        score = speech_clarity_score(_noise(1.0, amplitude=0.5))
        self.assertGreater(score, 5.0)
        self.assertLess(score, 30.0)


class ChipsetHintTest(unittest.TestCase):
    def test_known_chipsets(self) -> None:
        self.assertIn("dual", chipset_mic_hint("Qualcomm QCC30xx/51xx"))
        self.assertIn("beamforming", chipset_mic_hint("Apple H1/H2 (propietario)"))

    def test_unknown_returns_none(self) -> None:
        self.assertIsNone(chipset_mic_hint("Desconocido"))
        self.assertIsNone(chipset_mic_hint(None))


class FormatTest(unittest.TestCase):
    def test_report_contains_sections(self) -> None:
        profile = MicProfile(
            device_name="Maxell Dynamic+",
            input_channels=1,
            topology=interpret_topology(1),
            enc_status="Detected",
            enc_reduction_db=8.2,
            sensitivity_dbfs=-38.0,
            speech_clarity=68.0,
            chipset_hint="mono MEMS con ENC basico",
            estimated_hardware="1+ MEMS por auricular, uplink mezclado a mono",
            confidence=0.71,
        )
        text = format_mic_profile(profile)
        self.assertIn("=== MICROPHONE PROFILE ===", text)
        self.assertIn("Maxell Dynamic+", text)
        self.assertIn("ENC               : Detected (+8.2 dB)", text)
        self.assertIn("Confidence        : 0.71", text)


if __name__ == "__main__":
    unittest.main()
