"""
audio/dropout_test.py
---------------------
Deteccion de packet loss / dropouts por continuidad acustica.

Metodo:
    1. Se reproduce un tono continuo por los auriculares mientras se
       graba con el microfono (mismo reloj de audio que latency_test).
    2. La envolvente RMS por ventanas cortas (10 ms) revela huecos:
       una ventana que cae por debajo del umbral relativo dentro de la
       region activa es una discontinuidad (dropout).
    3. Se reporta: numero de dropouts, hueco total en ms y porcentaje
       de continuidad.

Esto detecta perdida de paquetes A2DP, interferencia y buffers pobres,
especialmente frecuentes en TWS economicos.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import datetime

import numpy as np

from audio.audio_test import make_tone

logger = logging.getLogger("lino.audio.dropout")

try:
    import sounddevice as sd

    _AUDIO_AVAILABLE = True
except (ImportError, OSError) as _exc:
    sd = None
    _AUDIO_AVAILABLE = False
    logger.warning("sounddevice no disponible: %s", _exc)

SAMPLE_RATE = 48_000
WINDOW_MS = 10           # resolucion de deteccion de huecos
DROP_THRESHOLD = 0.15    # ventana < 15 % de la mediana activa = dropout
NOISE_FLOOR = 1e-4       # silencio absoluto (mic sin senal)

# Ultima grabacion cruda (para guardarla en la sesion como waveform.wav).
_last_recording: np.ndarray | None = None


@dataclass
class DropoutResult:
    """Resultado del test de continuidad de audio."""

    dropouts: int
    total_gap_ms: float
    continuity_pct: float        # 100 = sin discontinuidades
    duration_s: float
    timestamp: str = field(
        default_factory=lambda: datetime.now().isoformat(timespec="seconds")
    )

    def format(self) -> str:
        return (
            f"{self.dropouts} dropout(s), hueco total {self.total_gap_ms:.0f} ms, "
            f"continuidad {self.continuity_pct:.1f}%"
        )


def analyze_continuity(
    recorded: np.ndarray,
    sample_rate: int = SAMPLE_RATE,
    window_ms: int = WINDOW_MS,
    threshold: float = DROP_THRESHOLD,
) -> DropoutResult | None:
    """Analiza la envolvente de una grabacion en busca de discontinuidades.

    Funcion pura (testeable sin hardware): recibe la senal grabada y
    devuelve el conteo de huecos dentro de la region activa del tono.
    """
    recorded = np.asarray(recorded, dtype=np.float64).flatten()
    win = max(1, int(sample_rate * window_ms / 1000))
    n_windows = len(recorded) // win
    if n_windows < 10:
        logger.warning("Grabacion demasiado corta para analizar continuidad")
        return None

    # Envolvente RMS por ventana.
    trimmed = recorded[: n_windows * win].reshape(n_windows, win)
    envelope = np.sqrt(np.mean(np.square(trimmed), axis=1))

    # Region activa: desde la primera hasta la ultima ventana con senal.
    active_idx = np.where(envelope > NOISE_FLOOR)[0]
    if active_idx.size < 5:
        logger.warning("Microfono sin senal: no se puede medir continuidad")
        return None
    start, end = int(active_idx[0]), int(active_idx[-1]) + 1
    active = envelope[start:end]

    reference = float(np.median(active))
    if reference <= NOISE_FLOOR:
        return None

    # Dropout = ventana activa que cae bajo el umbral relativo.
    is_gap = active < (threshold * reference)
    gap_windows = int(np.sum(is_gap))

    # Conteo de eventos: transiciones señal -> hueco.
    transitions = np.diff(is_gap.astype(int))
    dropouts = int(np.sum(transitions == 1)) + (1 if is_gap[0] else 0)

    total_gap_ms = gap_windows * window_ms
    continuity = 100.0 * (1.0 - gap_windows / len(active))
    result = DropoutResult(
        dropouts=dropouts,
        total_gap_ms=float(total_gap_ms),
        continuity_pct=float(continuity),
        duration_s=len(active) * window_ms / 1000.0,
    )
    logger.info("Continuidad: %s", result.format())
    return result


def dropout_test(duration: float = 5.0, frequency: float = 1000.0) -> DropoutResult | None:
    """Test completo: tono continuo + grabacion + analisis de huecos.

    Returns:
        DropoutResult o None si el hardware de audio no permitio medir.
    """
    global _last_recording
    if not _AUDIO_AVAILABLE:
        logger.error("Audio no disponible: no se puede medir continuidad")
        return None

    duration = min(max(duration, 1.0), 15.0)
    tone = 0.4 * make_tone(frequency, duration)
    stereo = np.column_stack([tone, tone])

    try:
        logger.info("Test de continuidad: tono %.0f Hz durante %.1f s...", frequency, duration)
        recorded = sd.playrec(stereo, SAMPLE_RATE, channels=1)
        sd.wait()
    except (sd.PortAudioError, ValueError) as exc:
        logger.error("Error en loopback de continuidad: %s", exc)
        return None

    _last_recording = recorded.flatten().astype(np.float32)
    return analyze_continuity(_last_recording)


def get_last_recording() -> np.ndarray | None:
    """Grabacion cruda del ultimo test (para waveform.wav de la sesion)."""
    return _last_recording
