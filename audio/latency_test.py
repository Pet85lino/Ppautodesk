"""
audio/latency_test.py
---------------------
Estimacion de latencia de audio por loopback acustico.

Metodo:
    1. Se reproduce un pulso corto (chirp) por los auriculares.
    2. Se graba simultaneamente con el microfono del equipo.
    3. La correlacion cruzada (scipy) entre senal emitida y grabada
       da el retardo total reproduccion + transmision Bluetooth.

Limitacion tecnica: incluye la latencia del propio stack de audio del
sistema operativo, por lo que el valor es una estimacion comparativa
(util para comparar codecs/auriculares, no una medida de laboratorio).
"""

from __future__ import annotations

import logging

import numpy as np
from scipy.signal import correlate

logger = logging.getLogger("lino.audio.latency")

try:
    import sounddevice as sd

    _AUDIO_AVAILABLE = True
except (ImportError, OSError) as _exc:
    sd = None
    _AUDIO_AVAILABLE = False
    logger.warning("sounddevice no disponible: %s", _exc)

SAMPLE_RATE = 48_000  # Hz


def _make_chirp(duration: float = 0.1) -> np.ndarray:
    """Pulso de barrido 1-4 kHz: facil de detectar sobre ruido ambiente."""
    t = np.linspace(0, duration, int(SAMPLE_RATE * duration), endpoint=False)
    freq = np.linspace(1000, 4000, len(t))
    return (0.5 * np.sin(2 * np.pi * freq * t)).astype(np.float32)


def estimate_latency(record_seconds: float = 1.5) -> float | None:
    """Estima la latencia de reproduccion en milisegundos.

    Returns:
        Latencia estimada en ms, o None si el test no pudo ejecutarse
        (sin microfono, sin PortAudio, o pulso no detectado).
    """
    if not _AUDIO_AVAILABLE:
        logger.error("Audio no disponible: no se puede medir latencia")
        return None

    chirp = _make_chirp()
    # Margen de silencio tras el pulso para capturar el retardo Bluetooth.
    padded = np.concatenate(
        [chirp, np.zeros(int(SAMPLE_RATE * (record_seconds - 0.1)), dtype=np.float32)]
    )

    try:
        logger.info("Midiendo latencia por loopback acustico (%.1f s)...", record_seconds)
        # playrec reproduce y graba en paralelo con el mismo reloj de audio.
        recorded = sd.playrec(padded, SAMPLE_RATE, channels=1)
        sd.wait()
    except sd.PortAudioError as exc:
        logger.error("Error en loopback de audio: %s", exc)
        return None

    recorded = recorded.flatten()
    if np.max(np.abs(recorded)) < 1e-4:
        logger.warning("Microfono sin senal: no se detecto el pulso")
        return None

    # Posicion del pico de correlacion = retardo en muestras.
    corr = correlate(recorded, chirp, mode="valid")
    delay_samples = int(np.argmax(np.abs(corr)))
    latency_ms = delay_samples * 1000.0 / SAMPLE_RATE

    logger.info("Latencia estimada: %.1f ms", latency_ms)
    return latency_ms
