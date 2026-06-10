"""
audio/audio_test.py
-------------------
Tests basicos de audio para auriculares conectados como salida del sistema.

Incluye en el MVP:
    * Listado de dispositivos de audio disponibles.
    * Tono de prueba senoidal por canal (L / R / ambos) -> test de balance.

`sounddevice` requiere PortAudio; si no esta disponible (p.ej. entorno sin
audio) las funciones devuelven errores controlados en lugar de romper la app.
"""

from __future__ import annotations

import logging

import numpy as np

logger = logging.getLogger("lino.audio.test")

try:
    import sounddevice as sd

    _AUDIO_AVAILABLE = True
except (ImportError, OSError) as _exc:  # OSError: PortAudio ausente
    sd = None
    _AUDIO_AVAILABLE = False
    logger.warning("sounddevice no disponible: %s", _exc)

SAMPLE_RATE = 48_000  # Hz, estandar para A2DP


def audio_available() -> bool:
    """Indica si el subsistema de audio esta operativo en este equipo."""
    return _AUDIO_AVAILABLE


def list_audio_devices() -> list[dict]:
    """Enumera los dispositivos de audio del sistema (entrada y salida).

    Util para verificar que los auriculares TWS estan enrutados como
    salida activa antes de ejecutar los tests.
    """
    if not _AUDIO_AVAILABLE:
        return []
    try:
        devices = sd.query_devices()
        return [
            {
                "index": i,
                "name": d["name"],
                "inputs": d["max_input_channels"],
                "outputs": d["max_output_channels"],
                "sample_rate": d["default_samplerate"],
            }
            for i, d in enumerate(devices)
        ]
    except sd.PortAudioError as exc:
        logger.error("Error consultando dispositivos de audio: %s", exc)
        return []


def play_test_tone(
    frequency: float = 440.0,
    duration: float = 1.5,
    channel: str = "both",
    volume: float = 0.4,
) -> bool:
    """Reproduce un tono senoidal de prueba.

    Args:
        frequency: frecuencia del tono en Hz (440 = La de referencia).
        duration: duracion en segundos.
        channel: "left", "right" o "both" -> base del test de balance L/R.
        volume: amplitud 0.0-1.0 (moderada por defecto para proteger oidos).

    Returns:
        True si el tono se reprodujo correctamente.
    """
    if not _AUDIO_AVAILABLE:
        logger.error("Audio no disponible en este sistema")
        return False

    t = np.linspace(0, duration, int(SAMPLE_RATE * duration), endpoint=False)
    tone = (volume * np.sin(2 * np.pi * frequency * t)).astype(np.float32)

    # Estereo con seleccion de canal: silencio absoluto en el canal opuesto
    # permite detectar drivers danados o desbalance fisico.
    stereo = np.zeros((len(tone), 2), dtype=np.float32)
    if channel in ("left", "both"):
        stereo[:, 0] = tone
    if channel in ("right", "both"):
        stereo[:, 1] = tone

    try:
        logger.info(
            "Reproduciendo tono %.0f Hz (%.1f s) canal=%s", frequency, duration, channel
        )
        sd.play(stereo, SAMPLE_RATE)
        sd.wait()
        return True
    except sd.PortAudioError as exc:
        logger.error("Error reproduciendo tono: %s", exc)
        return False


def balance_test(frequency: float = 440.0, duration: float = 1.0) -> bool:
    """Secuencia de balance: izquierda -> derecha -> ambos.

    El usuario verifica perceptualmente que ambos auriculares suenan
    con el mismo nivel (diagnostico rapido de driver danado).
    """
    ok = True
    for ch in ("left", "right", "both"):
        ok = play_test_tone(frequency, duration, channel=ch) and ok
    return ok
