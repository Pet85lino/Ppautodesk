"""
audio/spectrum.py
-----------------
Analizador de espectro: FFT y espectrograma de capturas de microfono.

Capacidades:
    * compute_spectrum     -> magnitud en dB por frecuencia (FFT + Hann).
    * compute_spectrogram  -> matriz tiempo-frecuencia (scipy).
    * capture_and_plot     -> captura N segundos del microfono y guarda
                              espectro + espectrograma (waterfall) en PNG.

Uso tipico: reproducir ruido rosa o un sweep por los auriculares y
capturar con el microfono para ver la respuesta en frecuencia real.
"""

from __future__ import annotations

import logging
from datetime import datetime
from pathlib import Path

import numpy as np
from scipy.signal import spectrogram as scipy_spectrogram
from scipy.signal.windows import hann

logger = logging.getLogger("lino.audio.spectrum")

try:
    import sounddevice as sd

    _AUDIO_AVAILABLE = True
except (ImportError, OSError) as _exc:
    sd = None
    _AUDIO_AVAILABLE = False
    logger.warning("sounddevice no disponible: %s", _exc)

SAMPLE_RATE = 48_000
EPS = 1e-12


def compute_spectrum(signal: np.ndarray, sample_rate: int = SAMPLE_RATE):
    """Espectro de magnitud en dB (FFT con ventana Hann).

    Returns:
        (freqs_hz, magnitude_db) — solo la mitad positiva del espectro.
    """
    signal = np.asarray(signal, dtype=np.float64).flatten()
    if signal.size == 0:
        return np.array([]), np.array([])
    windowed = signal * hann(len(signal))
    spectrum = np.abs(np.fft.rfft(windowed))
    freqs = np.fft.rfftfreq(len(signal), 1.0 / sample_rate)
    magnitude_db = 20.0 * np.log10(spectrum + EPS)
    return freqs, magnitude_db


def compute_spectrogram(signal: np.ndarray, sample_rate: int = SAMPLE_RATE):
    """Espectrograma (waterfall) en dB.

    Returns:
        (freqs_hz, times_s, power_db) listos para pcolormesh.
    """
    signal = np.asarray(signal, dtype=np.float64).flatten()
    freqs, times, sxx = scipy_spectrogram(
        signal, fs=sample_rate, nperseg=1024, noverlap=512
    )
    return freqs, times, 10.0 * np.log10(sxx + EPS)


def capture(duration: float = 3.0) -> np.ndarray | None:
    """Captura del microfono (mono) para analisis espectral."""
    if not _AUDIO_AVAILABLE:
        logger.error("Audio no disponible: no se puede capturar espectro")
        return None
    duration = min(max(duration, 0.5), 15.0)
    try:
        logger.info("Capturando %.1f s de microfono para espectro...", duration)
        recorded = sd.rec(int(SAMPLE_RATE * duration), SAMPLE_RATE, channels=1)
        sd.wait()
        return recorded.flatten()
    except (sd.PortAudioError, ValueError) as exc:
        logger.error("Error capturando audio: %s", exc)
        return None


def plot_analysis(signal: np.ndarray, out_dir: Path) -> Path | None:
    """Guarda espectro + espectrograma de una senal en un PNG tematizado."""
    try:
        import matplotlib

        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        logger.warning("matplotlib no disponible: sin grafica de espectro")
        return None

    freqs, mag_db = compute_spectrum(signal)
    sf, st, sxx_db = compute_spectrogram(signal)

    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(10, 7), facecolor="#0A1428")
    for ax in (ax1, ax2):
        ax.set_facecolor("#081020")
        ax.tick_params(colors="#7A8BA3", labelsize=8)
        for spine in ax.spines.values():
            spine.set_color("#1E2A44")

    ax1.semilogx(freqs[1:], mag_db[1:], color="#00E5FF", linewidth=0.7)
    ax1.set_xlim(20, 20_000)
    ax1.set_title("Espectro (FFT)", color="#E6F1FF")
    ax1.set_xlabel("Hz", color="#7A8BA3")
    ax1.set_ylabel("dB", color="#7A8BA3")

    mesh = ax2.pcolormesh(st, sf, sxx_db, shading="gouraud", cmap="magma")
    ax2.set_ylim(0, 20_000)
    ax2.set_title("Espectrograma (waterfall)", color="#E6F1FF")
    ax2.set_xlabel("s", color="#7A8BA3")
    ax2.set_ylabel("Hz", color="#7A8BA3")
    fig.colorbar(mesh, ax=ax2).ax.tick_params(colors="#7A8BA3", labelsize=7)

    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / f"spectrum_{datetime.now():%Y%m%d_%H%M%S}.png"
    fig.tight_layout()
    fig.savefig(out_path, dpi=110)
    plt.close(fig)
    logger.info("Analisis espectral guardado en %s", out_path)
    return out_path


def capture_and_plot(duration: float = 3.0, out_dir: Path | None = None) -> Path | None:
    """Captura microfono y genera el analisis espectral completo."""
    signal = capture(duration)
    if signal is None:
        return None
    from core.config_manager import PROJECT_ROOT

    return plot_analysis(signal, out_dir or (PROJECT_ROOT / "exports"))
