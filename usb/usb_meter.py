"""
usb/usb_meter.py
----------------
Lectura de medidores USB de carga (curvas voltaje/corriente/mAh).

Estado V1.2 (EXPERIMENTAL):
    * UM24C / UM25C (RDTech): protocolo serie implementado. El medidor
      responde a un byte de peticion (0xF0) con un dump binario de 130
      bytes que incluye voltaje, corriente, potencia, temperatura y
      capacidad acumulada.
    * FNB58 / TC66C: pendientes para V2 (el TC66 cifra el payload con
      AES y el FNB58 usa HID; requieren hardware para validar).

Cada muestra se modela como ChargeSample y se persiste en la tabla
charge_history para construir curvas de carga y estimar degradacion.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import datetime

logger = logging.getLogger("lino.usb.meter")

try:
    import serial

    _SERIAL_AVAILABLE = True
except ImportError as _exc:
    serial = None
    _SERIAL_AVAILABLE = False
    logger.warning("pyserial no disponible: %s", _exc)

# Protocolo UM24C/UM25C: peticion de dump completo.
UM_REQUEST_DUMP = bytes([0xF0])
UM_RESPONSE_SIZE = 130
UM_BAUDRATE = 9600
UM_TIMEOUT_S = 3.0

# Divisores del UM25C (el UM24C usa /100 y /1000 respectivamente).
UM25C_VOLTAGE_DIV = 1000.0   # 2 bytes -> V
UM25C_CURRENT_DIV = 10000.0  # 2 bytes -> A


@dataclass
class ChargeSample:
    """Muestra puntual de un medidor USB en linea con el case TWS."""

    source: str                      # modelo del medidor
    voltage_v: float
    current_a: float
    power_w: float
    capacity_mah: float | None = None
    temp_c: float | None = None
    timestamp: str = field(
        default_factory=lambda: datetime.now().isoformat(timespec="seconds")
    )

    def format(self) -> str:
        text = f"{self.voltage_v:.3f} V  {self.current_a:.4f} A  {self.power_w:.3f} W"
        if self.capacity_mah is not None:
            text += f"  {self.capacity_mah:.0f} mAh"
        if self.temp_c is not None:
            text += f"  {self.temp_c:.0f} C"
        return text


def meters_available() -> bool:
    """Indica si el soporte serie para medidores esta operativo."""
    return _SERIAL_AVAILABLE


def read_um25c(port: str, model: str = "UM25C") -> ChargeSample | None:
    """Lee una muestra de un RDTech UM24C/UM25C conectado por serie.

    Args:
        port: puerto serie (COM5, /dev/rfcomm0, ...).
        model: "UM25C" (por defecto) o "UM24C" (divisores distintos).

    Returns:
        ChargeSample o None si el medidor no respondio. EXPERIMENTAL:
        validado contra la documentacion publica del protocolo, pendiente
        de prueba con hardware real.
    """
    if not _SERIAL_AVAILABLE:
        logger.error("pyserial no disponible: no se puede leer el medidor")
        return None

    v_div = UM25C_VOLTAGE_DIV if model == "UM25C" else 100.0
    c_div = UM25C_CURRENT_DIV if model == "UM25C" else 1000.0

    try:
        with serial.Serial(port, UM_BAUDRATE, timeout=UM_TIMEOUT_S) as conn:
            conn.write(UM_REQUEST_DUMP)
            raw = conn.read(UM_RESPONSE_SIZE)
    except (serial.SerialException, OSError, ValueError) as exc:
        logger.error("Error leyendo %s en %s: %s", model, port, exc)
        return None

    if len(raw) < UM_RESPONSE_SIZE:
        logger.warning(
            "%s en %s: respuesta incompleta (%d/%d bytes)",
            model, port, len(raw), UM_RESPONSE_SIZE,
        )
        return None

    # Offsets documentados del dump UM24C/UM25C (big-endian).
    voltage = int.from_bytes(raw[2:4], "big") / v_div
    current = int.from_bytes(raw[4:6], "big") / c_div
    power = int.from_bytes(raw[6:10], "big") / 1000.0
    temp_c = float(int.from_bytes(raw[10:12], "big"))
    capacity_mah = float(int.from_bytes(raw[16:20], "big"))

    sample = ChargeSample(
        source=model,
        voltage_v=voltage,
        current_a=current,
        power_w=power,
        capacity_mah=capacity_mah,
        temp_c=temp_c,
    )
    logger.info("%s @ %s: %s", model, port, sample.format())
    return sample


def read_meter(port: str, meter_model: str) -> ChargeSample | None:
    """Despachador por modelo identificado en usb_monitor.KNOWN_METERS."""
    upper = meter_model.upper()
    if "UM25" in upper or "UM24" in upper:
        return read_um25c(port, model="UM25C" if "UM25" in upper else "UM24C")
    logger.info("Medidor %s aun sin protocolo implementado (V2)", meter_model)
    return None
