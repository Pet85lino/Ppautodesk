"""
audio/audio_test.py
-------------------
Tests de audio para auriculares conectados como salida del sistema.

Capacidades (V1.1):
    * Seleccion explicita del dispositivo de salida (evita reproducir las
      pruebas en los parlantes del sistema por error).
    * Listado filtrado: solo dispositivos con canales de salida.
    * Generadores de senal: tono senoidal, ruido blanco, ruido rosa y
      barrido logaritmico 20 Hz - 20 kHz (deteccion de resonancias).
    * Medicion RMS L/R por loopback (drivers deteriorados / desbalance).
    * Proteccion auditiva: volumen y duracion maximos aplicados SIEMPRE.
    * Compatibilidad async via run_async() (no bloquea BLE ni UI).

`sounddevice` requiere PortAudio; si no esta disponible las funciones
devuelven errores controlados en lugar de romper la aplicacion.
"""

from __future__ import annotations

import asyncio
import logging

import numpy as np
from scipy.signal import chirp as scipy_chirp

logger = logging.getLogger("lino.audio.test")

try:
    import sounddevice as sd

    _AUDIO_AVAILABLE = True
except (ImportError, OSError) as _exc:  # OSError: PortAudio ausente
    sd = None
    _AUDIO_AVAILABLE = False
    logger.warning("sounddevice no disponible: %s", _exc)

SAMPLE_RATE = 48_000  # Hz, estandar para A2DP

# ----------------------------------------------------------------------
# Proteccion auditiva: limites duros aplicados a TODA reproduccion.
# ----------------------------------------------------------------------
MAX_SAFE_VOLUME = 0.6     # amplitud maxima permitida (escala 0.0-1.0)
MAX_TEST_DURATION = 10.0  # segundos maximos por prueba

# Dispositivo de salida seleccionado (None = default del sistema).
_output_device: int | None = None


def audio_available() -> bool:
    """Indica si el subsistema de audio esta operativo en este equipo."""
    return _AUDIO_AVAILABLE


# ======================================================================
# Seleccion de dispositivo de salida
# ======================================================================
def list_output_devices() -> list[dict]:
    """Enumera SOLO dispositivos con canales de salida (auriculares,
    salidas Bluetooth, etc.), evitando ruido visual de microfonos."""
    if not _AUDIO_AVAILABLE:
        return []
    try:
        return [
            {
                "index": i,
                "name": d["name"],
                "outputs": d["max_output_channels"],
                "sample_rate": d["default_samplerate"],
            }
            for i, d in enumerate(sd.query_devices())
            if d["max_output_channels"] > 0
        ]
    except sd.PortAudioError as exc:
        logger.error("Error consultando dispositivos de audio: %s", exc)
        return []


def set_output_device(index: int | None) -> bool:
    """Fija el dispositivo de salida para todas las pruebas.

    Args:
        index: indice de sd.query_devices(), o None para volver al default.

    Returns:
        True si el dispositivo es valido y quedo seleccionado.
    """
    global _output_device
    if index is None:
        _output_device = None
        logger.info("Salida de audio: dispositivo por defecto del sistema")
        return True
    if not _AUDIO_AVAILABLE:
        return False
    try:
        info = sd.query_devices(index)
        if info["max_output_channels"] < 1:
            logger.error("El dispositivo %d no tiene canales de salida", index)
            return False
        _output_device = index
        logger.info("Salida de audio seleccionada: [%d] %s", index, info["name"])
        return True
    except (sd.PortAudioError, ValueError) as exc:
        logger.error("Dispositivo de salida %s invalido: %s", index, exc)
        return False


def get_output_device() -> int | None:
    """Indice del dispositivo de salida activo (None = default)."""
    return _output_device


# ======================================================================
# Generadores de senal (puros, testeables sin hardware de audio)
# ======================================================================
def make_tone(frequency: float, duration: float) -> np.ndarray:
    """Tono senoidal mono normalizado a amplitud 1.0."""
    t = np.linspace(0, duration, int(SAMPLE_RATE * duration), endpoint=False)
    return np.sin(2 * np.pi * frequency * t).astype(np.float32)


def make_white_noise(duration: float, seed: int | None = None) -> np.ndarray:
    """Ruido blanco gaussiano normalizado (deteccion de clipping y
    ruido interno de drivers)."""
    rng = np.random.default_rng(seed)
    noise = rng.standard_normal(int(SAMPLE_RATE * duration))
    return (noise / np.max(np.abs(noise))).astype(np.float32)


def make_pink_noise(duration: float, seed: int | None = None) -> np.ndarray:
    """Ruido rosa (energia -3 dB/octava) via conformado espectral 1/sqrt(f).

    Mas representativo del contenido musical que el ruido blanco; util
    para detectar distorsion en drivers a niveles de escucha reales.
    """
    n = int(SAMPLE_RATE * duration)
    rng = np.random.default_rng(seed)
    spectrum = np.fft.rfft(rng.standard_normal(n))
    freqs = np.fft.rfftfreq(n, 1.0 / SAMPLE_RATE)
    scale = np.ones_like(freqs)
    scale[1:] = 1.0 / np.sqrt(freqs[1:])
    scale[0] = 0.0  # sin componente DC
    pink = np.fft.irfft(spectrum * scale, n)
    return (pink / np.max(np.abs(pink))).astype(np.float32)


def make_sweep(duration: float = 6.0, f0: float = 20.0, f1: float = 20_000.0) -> np.ndarray:
    """Barrido logaritmico 20 Hz - 20 kHz (scipy.signal.chirp).

    Permite detectar resonancias, caidas de frecuencia y desbalance
    fisico recorriendo todo el rango audible.
    """
    t = np.linspace(0, duration, int(SAMPLE_RATE * duration), endpoint=False)
    sweep = scipy_chirp(t, f0=f0, t1=duration, f1=f1, method="logarithmic")
    return sweep.astype(np.float32)


def _to_stereo(mono: np.ndarray, channel: str, volume: float) -> np.ndarray:
    """Senal estereo con seleccion de canal y limites de seguridad.

    Silencio absoluto en el canal opuesto permite detectar drivers
    danados o desbalance fisico.
    """
    volume = min(max(volume, 0.0), MAX_SAFE_VOLUME)  # proteccion auditiva
    stereo = np.zeros((len(mono), 2), dtype=np.float32)
    if channel in ("left", "both"):
        stereo[:, 0] = volume * mono
    if channel in ("right", "both"):
        stereo[:, 1] = volume * mono
    return stereo


def _clamp_duration(duration: float) -> float:
    """Proteccion auditiva: ninguna prueba supera MAX_TEST_DURATION."""
    return min(max(duration, 0.05), MAX_TEST_DURATION)


# ======================================================================
# Reproduccion
# ======================================================================
def _play(stereo: np.ndarray) -> bool:
    """Reproduccion bloqueante en el dispositivo seleccionado."""
    if not _AUDIO_AVAILABLE:
        logger.error("Audio no disponible en este sistema")
        return False
    try:
        sd.play(stereo, SAMPLE_RATE, device=_output_device)
        sd.wait()
        return True
    except (sd.PortAudioError, ValueError) as exc:
        logger.error("Error reproduciendo senal: %s", exc)
        return False


def play_test_tone(
    frequency: float = 440.0,
    duration: float = 1.5,
    channel: str = "both",
    volume: float = 0.4,
) -> bool:
    """Reproduce un tono senoidal de prueba en el canal indicado."""
    duration = _clamp_duration(duration)
    logger.info(
        "Tono %.0f Hz (%.1f s) canal=%s vol=%.2f", frequency, duration, channel, volume
    )
    return _play(_to_stereo(make_tone(frequency, duration), channel, volume))


def play_white_noise(duration: float = 2.0, channel: str = "both", volume: float = 0.3) -> bool:
    """Ruido blanco: revela clipping, drivers danados y ruido interno."""
    duration = _clamp_duration(duration)
    logger.info("Ruido blanco (%.1f s) canal=%s", duration, channel)
    return _play(_to_stereo(make_white_noise(duration), channel, volume))


def play_pink_noise(duration: float = 2.0, channel: str = "both", volume: float = 0.35) -> bool:
    """Ruido rosa: prueba de distorsion a espectro tipo musical."""
    duration = _clamp_duration(duration)
    logger.info("Ruido rosa (%.1f s) canal=%s", duration, channel)
    return _play(_to_stereo(make_pink_noise(duration), channel, volume))


def play_sweep(duration: float = 6.0, channel: str = "both", volume: float = 0.35) -> bool:
    """Sweep 20 Hz - 20 kHz: resonancias y fallas de respuesta."""
    duration = _clamp_duration(duration)
    logger.info("Sweep 20 Hz - 20 kHz (%.1f s) canal=%s", duration, channel)
    return _play(_to_stereo(make_sweep(duration), channel, volume))


def balance_test(frequency: float = 440.0, duration: float = 1.0) -> bool:
    """Secuencia perceptual de balance: izquierda -> derecha -> ambos."""
    ok = True
    for ch in ("left", "right", "both"):
        ok = play_test_tone(frequency, duration, channel=ch) and ok
    return ok


# ======================================================================
# Medicion RMS L/R (objetiva, por loopback acustico)
# ======================================================================
def measure_rms_balance(frequency: float = 440.0, duration: float = 1.0) -> dict | None:
    """Compara la potencia RMS captada al excitar cada canal por separado.

    Reproduce un tono primero solo por la izquierda y luego solo por la
    derecha, grabando con el microfono. Una diferencia grande indica
    driver deteriorado o perdida de volumen en un lado.

    Returns:
        dict {rms_left, rms_right, diff_db} o None si no pudo medirse.
    """
    if not _AUDIO_AVAILABLE:
        logger.error("Audio no disponible: no se puede medir RMS")
        return None

    duration = _clamp_duration(duration)
    tone = make_tone(frequency, duration)
    results: dict[str, float] = {}

    for ch in ("left", "right"):
        stereo = _to_stereo(tone, ch, volume=0.4)
        try:
            recorded = sd.playrec(
                stereo, SAMPLE_RATE, channels=1, device=_output_device
            )
            sd.wait()
        except (sd.PortAudioError, ValueError) as exc:
            logger.error("Error en loopback RMS (%s): %s", ch, exc)
            return None
        rms = float(np.sqrt(np.mean(np.square(recorded.flatten()))))
        results[ch] = rms

    if results["left"] < 1e-6 or results["right"] < 1e-6:
        logger.warning("RMS: microfono sin senal, medicion no valida")
        return None

    diff_db = 20.0 * float(np.log10(results["left"] / results["right"]))
    logger.info(
        "RMS L=%.5f R=%.5f diff=%.1f dB", results["left"], results["right"], diff_db
    )
    return {"rms_left": results["left"], "rms_right": results["right"], "diff_db": diff_db}


# ======================================================================
# Compatibilidad async
# ======================================================================
async def run_async(func, /, *args, **kwargs):
    """Ejecuta cualquier test de este modulo sin bloquear el event loop.

    Ejemplo:
        await run_async(play_sweep, duration=4.0)

    Permite correr pruebas de audio en paralelo con escaneo BLE y UI.
    """
    return await asyncio.to_thread(func, *args, **kwargs)
