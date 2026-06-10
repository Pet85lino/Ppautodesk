"""
audio/latency_test.py
---------------------
Medicion avanzada de latencia de audio por loopback acustico.

Pipeline DSP (V1.1):
    1. Chirp logaritmico generado con scipy.signal.chirp + ventana Hann
       en los bordes (sin clics, correlacion mas limpia).
    2. Reproduccion y grabacion simultaneas con el mismo reloj de audio.
    3. Normalizacion de ambas senales (robustez frente a ruido ambiente).
    4. Correlacion cruzada via FFT (method='fft': rapida y escalable).
    5. Confidence score basado en peak-to-sidelobe ratio del pico.

Capacidades:
    * measure_once()      -> una medicion con confianza, por canal L/R/ambos.
    * jitter_test()       -> N mediciones: promedio, desviacion, estabilidad.
    * stereo_sync_test()  -> diferencia de latencia L vs R (drift TWS).
    * classify_latency()  -> clasificacion automatica (Excelente...Alta).
    * save_diagnostics_plot() -> waveform + correlacion en PNG (matplotlib).

Limitacion tecnica: el valor incluye la latencia del stack de audio del
sistema operativo; es una estimacion comparativa entre codecs/auriculares,
no una medida de laboratorio.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

import numpy as np
from scipy.signal import chirp as scipy_chirp
from scipy.signal import correlate
from scipy.signal.windows import hann

logger = logging.getLogger("lino.audio.latency")

try:
    import sounddevice as sd

    _AUDIO_AVAILABLE = True
except (ImportError, OSError) as _exc:
    sd = None
    _AUDIO_AVAILABLE = False
    logger.warning("sounddevice no disponible: %s", _exc)

SAMPLE_RATE = 48_000  # Hz
CHIRP_DURATION = 0.1  # s
CHIRP_F0, CHIRP_F1 = 1_000.0, 4_000.0  # banda facil de detectar sobre ruido

# Umbral minimo de confianza para aceptar una medicion como valida.
MIN_CONFIDENCE = 0.25


@dataclass
class LatencyResult:
    """Resultado de una medicion individual de latencia."""

    latency_ms: float
    confidence: float           # 0.0-1.0 (peak-to-sidelobe normalizado)
    channel: str                # "left" | "right" | "both"
    classification: str
    valid: bool
    timestamp: str = field(
        default_factory=lambda: datetime.now().isoformat(timespec="seconds")
    )


@dataclass
class JitterResult:
    """Resultado agregado de multiples mediciones consecutivas."""

    mean_ms: float
    std_ms: float               # jitter: estabilidad temporal del enlace BT
    runs: int
    valid_runs: int
    classification: str
    samples: list[float] = field(default_factory=list)


def classify_latency(latency_ms: float) -> str:
    """Clasificacion automatica de latencia para el usuario final."""
    if latency_ms < 80:
        return "Excelente"
    if latency_ms < 150:
        return "Buena"
    if latency_ms < 250:
        return "Normal"
    if latency_ms <= 300:
        return "Elevada"
    return "Alta"


# ======================================================================
# Generacion y analisis DSP (puros, testeables sin hardware)
# ======================================================================
def make_chirp(duration: float = CHIRP_DURATION) -> np.ndarray:
    """Chirp logaritmico 1-4 kHz con fundido Hann en los bordes.

    scipy.signal.chirp garantiza fase continua (DSP correcto); la ventana
    en los extremos elimina clics que ensucian la correlacion.
    """
    t = np.linspace(0, duration, int(SAMPLE_RATE * duration), endpoint=False)
    signal = scipy_chirp(t, f0=CHIRP_F0, t1=duration, f1=CHIRP_F1, method="logarithmic")

    # Fundido de entrada/salida: 10 % de la duracion en cada extremo.
    fade = max(1, int(0.1 * len(signal)))
    window = hann(2 * fade)
    signal[:fade] *= window[:fade]
    signal[-fade:] *= window[fade:]
    return (0.5 * signal).astype(np.float32)


def _normalize(signal: np.ndarray) -> np.ndarray:
    """Media cero y energia unitaria: reduce falsos positivos por ruido."""
    signal = signal - np.mean(signal)
    norm = np.linalg.norm(signal)
    return signal / norm if norm > 1e-12 else signal


def analyze_recording(recorded: np.ndarray, chirp: np.ndarray) -> tuple[float, float, np.ndarray]:
    """Localiza el chirp en la grabacion y calcula la confianza del pico.

    Returns:
        (delay_ms, confidence, correlacion) — confidence en 0.0-1.0 via
        peak-to-sidelobe ratio (pico claro = medicion fiable).
    """
    rec_n = _normalize(recorded.astype(np.float64))
    chirp_n = _normalize(chirp.astype(np.float64))

    # Correlacion FFT: O(n log n) frente a O(n^2) del metodo directo.
    corr = correlate(rec_n, chirp_n, mode="valid", method="fft")
    abs_corr = np.abs(corr)

    peak_idx = int(np.argmax(abs_corr))
    peak_val = float(abs_corr[peak_idx])

    # Sidelobes: todo excepto +-5 ms alrededor del pico principal.
    guard = int(0.005 * SAMPLE_RATE)
    mask = np.ones_like(abs_corr, dtype=bool)
    mask[max(0, peak_idx - guard): peak_idx + guard] = False
    sidelobes = abs_corr[mask]

    if sidelobes.size and float(np.std(sidelobes)) > 1e-12:
        psr = (peak_val - float(np.mean(sidelobes))) / float(np.std(sidelobes))
    else:
        psr = 0.0
    confidence = float(min(1.0, max(0.0, psr / 20.0)))  # PSR 20 sigma => 1.0

    delay_ms = peak_idx * 1000.0 / SAMPLE_RATE
    return delay_ms, confidence, corr


# ======================================================================
# Mediciones
# ======================================================================
def measure_once(channel: str = "both", record_seconds: float = 1.5) -> LatencyResult | None:
    """Una medicion de latencia por loopback en el canal indicado.

    Args:
        channel: "left", "right" o "both" -> permite medir cada auricular
                 por separado (deteccion de drift en TWS).

    Returns:
        LatencyResult (valid=False si la confianza es insuficiente),
        o None si el hardware de audio no permitio la medicion.
    """
    if not _AUDIO_AVAILABLE:
        logger.error("Audio no disponible: no se puede medir latencia")
        return None

    chirp = make_chirp()
    silence = np.zeros(
        max(0, int(SAMPLE_RATE * record_seconds) - len(chirp)), dtype=np.float32
    )
    mono = np.concatenate([chirp, silence])

    # Enrutado por canal: silencio absoluto en el canal no medido.
    stereo = np.zeros((len(mono), 2), dtype=np.float32)
    if channel in ("left", "both"):
        stereo[:, 0] = mono
    if channel in ("right", "both"):
        stereo[:, 1] = mono

    try:
        logger.info("Midiendo latencia (canal=%s, %.1f s)...", channel, record_seconds)
        recorded = sd.playrec(stereo, SAMPLE_RATE, channels=1)
        sd.wait()
    except (sd.PortAudioError, ValueError) as exc:
        logger.error("Error en loopback de audio: %s", exc)
        return None

    recorded = recorded.flatten()
    if np.max(np.abs(recorded)) < 1e-4:
        logger.warning("Microfono sin senal: no se detecto el pulso")
        return None

    delay_ms, confidence, _ = analyze_recording(recorded, chirp)
    valid = confidence >= MIN_CONFIDENCE
    result = LatencyResult(
        latency_ms=delay_ms,
        confidence=confidence,
        channel=channel,
        classification=classify_latency(delay_ms),
        valid=valid,
    )
    logger.info(
        "Latencia %s: %.1f ms (confianza %.2f, %s)%s",
        channel,
        delay_ms,
        confidence,
        result.classification,
        "" if valid else " [DESCARTADA: confianza baja]",
    )
    return result


def jitter_test(runs: int = 5, channel: str = "both") -> JitterResult | None:
    """Estabilidad temporal: N mediciones consecutivas.

    El jitter (desviacion estandar) es critico en Bluetooth: un enlace
    inestable produce variaciones grandes entre mediciones aunque el
    promedio sea aceptable.
    """
    samples: list[float] = []
    for i in range(runs):
        result = measure_once(channel=channel)
        if result is None:
            logger.error("Jitter test abortado en la corrida %d", i + 1)
            return None
        if result.valid:
            samples.append(result.latency_ms)

    if not samples:
        logger.warning("Jitter test: ninguna medicion valida")
        return None

    mean_ms = float(np.mean(samples))
    std_ms = float(np.std(samples))
    result = JitterResult(
        mean_ms=mean_ms,
        std_ms=std_ms,
        runs=runs,
        valid_runs=len(samples),
        classification=classify_latency(mean_ms),
        samples=samples,
    )
    logger.info(
        "Jitter: media=%.1f ms, sigma=%.1f ms (%d/%d validas, %s)",
        mean_ms, std_ms, len(samples), runs, result.classification,
    )
    return result


def stereo_sync_test() -> dict | None:
    """Diferencia de latencia entre canal izquierdo y derecho.

    En auriculares TWS cada lado mantiene su propio enlace; una
    diferencia sostenida indica drift o problemas de sincronizacion.
    """
    left = measure_once(channel="left")
    right = measure_once(channel="right")
    if not left or not right or not (left.valid and right.valid):
        logger.warning("Sync L/R: mediciones insuficientes o de baja confianza")
        return None

    drift_ms = left.latency_ms - right.latency_ms
    logger.info(
        "Sync L/R: L=%.1f ms, R=%.1f ms, drift=%.1f ms",
        left.latency_ms, right.latency_ms, drift_ms,
    )
    return {
        "left_ms": left.latency_ms,
        "right_ms": right.latency_ms,
        "drift_ms": drift_ms,
    }


# ======================================================================
# Visualizacion DSP
# ======================================================================
def save_diagnostics_plot(
    recorded: np.ndarray, chirp: np.ndarray, out_dir: Path | None = None
) -> Path | None:
    """Genera un PNG con waveform grabada + correlacion cruzada.

    Usa el backend Agg (sin ventana): seguro desde hilos de trabajo.
    Devuelve la ruta del archivo o None si matplotlib no esta disponible.
    """
    try:
        import matplotlib

        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        logger.warning("matplotlib no disponible: sin grafica de diagnostico")
        return None

    delay_ms, confidence, corr = analyze_recording(recorded, chirp)
    t_rec = np.arange(len(recorded)) * 1000.0 / SAMPLE_RATE
    t_corr = np.arange(len(corr)) * 1000.0 / SAMPLE_RATE

    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(10, 6), facecolor="#0A1428")
    for ax in (ax1, ax2):
        ax.set_facecolor("#081020")
        ax.tick_params(colors="#7A8BA3")
        for spine in ax.spines.values():
            spine.set_color("#1E2A44")

    ax1.plot(t_rec, recorded, color="#00E5FF", linewidth=0.6)
    ax1.set_title("Senal grabada (loopback)", color="#E6F1FF")
    ax1.set_xlabel("ms", color="#7A8BA3")

    ax2.plot(t_corr, np.abs(corr), color="#FF2E97", linewidth=0.8)
    ax2.axvline(delay_ms, color="#2EE6A8", linestyle="--", linewidth=1)
    ax2.set_title(
        f"Correlacion cruzada - pico en {delay_ms:.1f} ms (confianza {confidence:.2f})",
        color="#E6F1FF",
    )
    ax2.set_xlabel("ms", color="#7A8BA3")

    out_dir = out_dir or (Path(__file__).resolve().parent.parent / "logs")
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / f"latency_diag_{datetime.now():%Y%m%d_%H%M%S}.png"

    fig.tight_layout()
    fig.savefig(out_path, dpi=110)
    plt.close(fig)
    logger.info("Grafica de diagnostico guardada en %s", out_path)
    return out_path


def estimate_latency(record_seconds: float = 1.5) -> float | None:
    """API retro-compatible (V1.0): latencia simple en ms o None."""
    result = measure_once(record_seconds=record_seconds)
    return result.latency_ms if result and result.valid else None
