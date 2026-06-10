"""
audio/mic_profile.py
--------------------
Perfilado del microfono uplink de auriculares Bluetooth.

Metodos implementados (todos heuristicos, con confianza explicita):

    1. Canales de entrada expuestos -> topologia (mono / dual / array).
       OJO: 1 canal expuesto != 1 microfono fisico (el firmware mezcla
       y el SO abstrae; HFP simplifica a mono casi siempre).
    2. Deteccion ENC: grabacion de ruido constante; si el piso de ruido
       cae en los primeros segundos, el DSP esta adaptando (supresion).
    3. Sensibilidad: nivel RMS en dBFS de la captura ambiente.
    4. Claridad de voz: concentracion de energia en la banda de voz
       (300-3400 Hz) tipica del uplink HFP filtrado.
    5. Pista por chipset: topologia tipica segun firmware_profiler.

LIMITES (no medibles sin teardown): numero fisico exacto de MEMS,
modelo del capsulado, cableado interno. El USB-C del case solo carga:
no expone datos del TWS salvo modos factory/UART rarisimos.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import datetime

import numpy as np

logger = logging.getLogger("lino.audio.mic")

try:
    import sounddevice as sd

    _AUDIO_AVAILABLE = True
except (ImportError, OSError) as _exc:
    sd = None
    _AUDIO_AVAILABLE = False
    logger.warning("sounddevice no disponible: %s", _exc)

SAMPLE_RATE = 48_000

# Banda de voz del uplink HFP/mSBC.
SPEECH_BAND_HZ = (300.0, 3400.0)

# Umbrales de deteccion ENC (caida del piso de ruido inicial -> estable).
ENC_DETECTED_DB = 6.0
ENC_POSSIBLE_DB = 3.0

# Topologia tipica de microfonos por chipset (heuristica, especulativa).
MIC_HINTS_BY_CHIPSET: list[tuple[str, str]] = [
    ("Qualcomm QCC", "dual MEMS con ENC (cVc tipico)"),
    ("Airoha", "dual MEMS con ENC"),
    ("BES", "mono o dual MEMS"),
    ("Realtek", "mono MEMS con ENC basico"),
    ("Apple", "array multi-mic con beamforming"),
]

# Dispositivo de entrada seleccionado (None = default del sistema).
_input_device: int | None = None


@dataclass
class MicProfile:
    """Resultado del perfilado de microfono (serializable a sesion)."""

    device_name: str
    input_channels: int | None
    topology: str
    enc_status: str                 # Detected | Possible | Not detected | Unknown
    enc_reduction_db: float | None
    sensitivity_dbfs: float | None
    speech_clarity: float | None    # 0-100
    chipset_hint: str | None
    estimated_hardware: str
    confidence: float               # 0.0-1.0
    timestamp: str = field(
        default_factory=lambda: datetime.now().isoformat(timespec="seconds")
    )


# ======================================================================
# Metodo 1: canales de entrada expuestos
# ======================================================================
def list_input_devices() -> list[dict]:
    """Dispositivos con canales de ENTRADA (microfonos visibles al SO)."""
    if not _AUDIO_AVAILABLE:
        return []
    try:
        return [
            {
                "index": i,
                "name": d["name"],
                "inputs": d["max_input_channels"],
                "sample_rate": d["default_samplerate"],
            }
            for i, d in enumerate(sd.query_devices())
            if d["max_input_channels"] > 0
        ]
    except sd.PortAudioError as exc:
        logger.error("Error consultando entradas de audio: %s", exc)
        return []


def set_input_device(index: int | None) -> bool:
    """Selecciona el microfono a perfilar (None = default)."""
    global _input_device
    if index is None:
        _input_device = None
        return True
    if not _AUDIO_AVAILABLE:
        return False
    try:
        info = sd.query_devices(index)
        if info["max_input_channels"] < 1:
            logger.error("El dispositivo %d no tiene canales de entrada", index)
            return False
        _input_device = index
        logger.info("Entrada seleccionada: [%d] %s", index, info["name"])
        return True
    except (sd.PortAudioError, ValueError) as exc:
        logger.error("Entrada %s invalida: %s", index, exc)
        return False


def interpret_topology(channels: int | None) -> str:
    """Interpretacion honesta de los canales expuestos por el SO."""
    if channels is None or channels < 1:
        return "desconocida (sin entrada visible)"
    if channels == 1:
        return "uplink mono expuesto (1+ mics fisicos posibles)"
    if channels == 2:
        return "uplink dual/stereo expuesto (dual mic probable)"
    return f"array DSP expuesto ({channels} canales)"


# ======================================================================
# Metodo 2: deteccion ENC (analisis puro, testeable)
# ======================================================================
def analyze_enc(recorded: np.ndarray, sample_rate: int = SAMPLE_RATE) -> dict | None:
    """Detecta supresion de ruido adaptativa en una grabacion de ruido
    ambiente CONSTANTE.

    Un DSP con ENC converge en los primeros segundos: el piso de ruido
    del tramo inicial es mayor que el del tramo estable.

    Returns:
        {"reduction_db": float, "status": str} o None si la grabacion
        es demasiado corta o muda.
    """
    signal = np.asarray(recorded, dtype=np.float64).flatten()
    if len(signal) < int(2.0 * sample_rate):
        logger.warning("ENC: grabacion demasiado corta (min 2 s)")
        return None

    head = signal[: int(0.7 * sample_rate)]
    tail = signal[int(1.5 * sample_rate):]
    rms_head = float(np.sqrt(np.mean(np.square(head))))
    rms_tail = float(np.sqrt(np.mean(np.square(tail))))
    if rms_head < 1e-6 or rms_tail < 1e-6:
        logger.warning("ENC: microfono sin senal")
        return None

    reduction_db = 20.0 * float(np.log10(rms_head / rms_tail))
    if reduction_db >= ENC_DETECTED_DB:
        status = "Detected"
    elif reduction_db >= ENC_POSSIBLE_DB:
        status = "Possible"
    else:
        status = "Not detected"
    logger.info("ENC: reduccion %.1f dB -> %s", reduction_db, status)
    return {"reduction_db": round(reduction_db, 1), "status": status}


# ======================================================================
# Metodos 3-4: sensibilidad y claridad de voz (analisis puro)
# ======================================================================
def sensitivity_dbfs(recorded: np.ndarray) -> float | None:
    """Nivel RMS de la captura en dBFS (sensibilidad aparente)."""
    signal = np.asarray(recorded, dtype=np.float64).flatten()
    rms = float(np.sqrt(np.mean(np.square(signal))))
    if rms < 1e-9:
        return None
    return round(20.0 * float(np.log10(rms)), 1)


def speech_clarity_score(recorded: np.ndarray, sample_rate: int = SAMPLE_RATE) -> float | None:
    """Concentracion de energia en la banda de voz (300-3400 Hz), 0-100.

    Un uplink HFP bien filtrado concentra la energia en esa banda; un
    mic ruidoso o sin DSP reparte energia fuera de ella.
    """
    signal = np.asarray(recorded, dtype=np.float64).flatten()
    if len(signal) < 1024:
        return None
    spectrum = np.abs(np.fft.rfft(signal)) ** 2
    freqs = np.fft.rfftfreq(len(signal), 1.0 / sample_rate)
    total = float(np.sum(spectrum[1:]))  # sin DC
    if total <= 0:
        return None
    lo, hi = SPEECH_BAND_HZ
    band = float(np.sum(spectrum[(freqs >= lo) & (freqs <= hi)]))
    return round(100.0 * band / total, 1)


def chipset_mic_hint(probable_chipset: str | None) -> str | None:
    """Topologia tipica de microfonos para un chipset inferido."""
    if not probable_chipset:
        return None
    for key, hint in MIC_HINTS_BY_CHIPSET:
        if key.lower() in probable_chipset.lower():
            return hint
    return None


# ======================================================================
# Captura + perfil completo
# ======================================================================
def _record(duration: float) -> np.ndarray | None:
    if not _AUDIO_AVAILABLE:
        logger.error("Audio no disponible: no se puede grabar")
        return None
    try:
        recorded = sd.rec(
            int(SAMPLE_RATE * duration), SAMPLE_RATE,
            channels=1, device=_input_device,
        )
        sd.wait()
        return recorded.flatten()
    except (sd.PortAudioError, ValueError) as exc:
        logger.error("Error grabando: %s", exc)
        return None


def microphone_profile(
    duration: float = 4.0,
    device_name: str = "(entrada por defecto)",
    probable_chipset: str | None = None,
) -> MicProfile | None:
    """Perfil completo del microfono activo.

    Procedimiento: mantener un ruido ambiente CONSTANTE (ventilador,
    ruido blanco por parlantes) durante la captura; el analisis ENC
    depende de que la fuente no cambie.

    Returns:
        MicProfile o None si no hay entrada de audio disponible.
    """
    duration = min(max(duration, 2.0), 15.0)

    # Canales expuestos del dispositivo activo.
    channels: int | None = None
    if _AUDIO_AVAILABLE:
        try:
            info = sd.query_devices(_input_device, "input")
            channels = int(info["max_input_channels"])
            device_name = str(info["name"])
        except (sd.PortAudioError, ValueError, TypeError):
            channels = None

    recorded = _record(duration)
    if recorded is None:
        return None

    enc = analyze_enc(recorded)
    sens = sensitivity_dbfs(recorded)
    clarity = speech_clarity_score(recorded)
    hint = chipset_mic_hint(probable_chipset)

    # Confianza honesta: crece con cada medicion disponible, nunca > 0.85
    # (sin teardown no hay certeza de hardware).
    confidence = 0.35
    for available in (channels is not None, enc is not None,
                      sens is not None, clarity is not None, hint is not None):
        if available:
            confidence += 0.1
    confidence = min(0.85, confidence)

    if channels == 1:
        hardware = "1+ MEMS por auricular, uplink mezclado a mono"
    elif channels and channels >= 2:
        hardware = "dual MEMS o array con uplink multicanal"
    else:
        hardware = "no determinable desde el host"
    if hint:
        hardware += f" | tipico del chipset: {hint}"

    profile = MicProfile(
        device_name=device_name,
        input_channels=channels,
        topology=interpret_topology(channels),
        enc_status=enc["status"] if enc else "Unknown",
        enc_reduction_db=enc["reduction_db"] if enc else None,
        sensitivity_dbfs=sens,
        speech_clarity=clarity,
        chipset_hint=hint,
        estimated_hardware=hardware,
        confidence=round(confidence, 2),
    )
    logger.info("Perfil de microfono: %s", format_mic_profile(profile))
    return profile


def format_mic_profile(profile: MicProfile) -> str:
    """Reporte de texto '=== MICROPHONE PROFILE ==='."""
    lines = [
        "=== MICROPHONE PROFILE ===",
        f"Device            : {profile.device_name}",
        f"Input channels    : {profile.input_channels if profile.input_channels else 'N/D'}",
        f"Topology          : {profile.topology}",
        f"ENC               : {profile.enc_status}"
        + (f" ({profile.enc_reduction_db:+.1f} dB)" if profile.enc_reduction_db is not None else ""),
        f"Mic sensitivity   : {f'{profile.sensitivity_dbfs} dBFS' if profile.sensitivity_dbfs is not None else 'N/D'}",
        f"Speech clarity    : {f'{profile.speech_clarity}/100' if profile.speech_clarity is not None else 'N/D'}",
        f"Estimated hardware: {profile.estimated_hardware}",
        f"Confidence        : {profile.confidence:.2f}",
    ]
    return "\n".join(lines)
