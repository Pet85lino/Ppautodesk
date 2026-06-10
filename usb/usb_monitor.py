"""
usb/usb_monitor.py
------------------
Monitoreo energetico del equipo anfitrion y deteccion de medidores USB.

Capacidades (V1.1):
    * PowerStatus tipado (dataclass) en lugar de dicts genericos.
    * Deteccion dinamica de puertos serie (pyserial) con identificacion
      de medidores USB conocidos: UM25C, FNB58, TC66C, AT34.
    * Sistema de alertas basico: descarga anormal, bateria critica,
      cambios de estado de carga.

En V2+ se implementara el protocolo de lectura de cada medidor para
obtener voltaje, corriente, potencia y mAh reales del case.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import datetime

import psutil

logger = logging.getLogger("lino.usb.monitor")

try:
    from serial.tools import list_ports

    _SERIAL_AVAILABLE = True
except ImportError as _exc:
    list_ports = None
    _SERIAL_AVAILABLE = False
    logger.warning("pyserial no disponible: %s", _exc)

# Palabras clave para identificar medidores USB comerciales por la
# descripcion/VID del puerto serie. Se ampliara con protocolos en V2.
KNOWN_METERS: dict[str, str] = {
    "UM25C": "RDTech UM25C",
    "UM24C": "RDTech UM24C",
    "FNB58": "FNIRSI FNB58",
    "FNB48": "FNIRSI FNB48",
    "TC66": "RDTech TC66C",
    "AT34": "RDTech AT34",
    "CP210": "Puente serie (posible medidor)",
    "CH340": "Puente serie (posible medidor)",
}

# Umbrales del sistema de alertas.
CRITICAL_BATTERY_PERCENT = 10.0
ABNORMAL_DISCHARGE_PCT_PER_MIN = 1.5


@dataclass
class PowerStatus:
    """Estado de energia del sistema anfitrion (tipado y serializable)."""

    available: bool                 # hay sensor de bateria
    percent: float | None = None    # porcentaje de carga
    plugged: bool | None = None     # conectado a corriente/USB
    secs_left: int | None = None    # autonomia estimada en segundos
    timestamp: str = field(
        default_factory=lambda: datetime.now().isoformat(timespec="seconds")
    )

    def format(self) -> str:
        """Texto legible para el dashboard."""
        if not self.available:
            return "Sin sensor de bateria (equipo de escritorio)"
        state = "Cargando (USB/AC)" if self.plugged else "Descargando"
        text = f"{self.percent:.0f}% - {state}"
        if self.secs_left:
            text += f" - {self.secs_left // 60} min restantes"
        return text


@dataclass
class SerialPortInfo:
    """Puerto serie detectado, con identificacion de medidor si aplica."""

    device: str          # p.ej. COM3 o /dev/ttyUSB0
    description: str
    meter_model: str | None = None  # modelo identificado o None

    def format(self) -> str:
        tag = f" [{self.meter_model}]" if self.meter_model else ""
        return f"{self.device} - {self.description}{tag}"


def get_power_status() -> PowerStatus:
    """Estado de energia del sistema anfitrion via psutil."""
    try:
        battery = psutil.sensors_battery()
    except (AttributeError, OSError) as exc:
        logger.warning("Sensor de bateria no accesible: %s", exc)
        battery = None

    if battery is None:
        # Equipos de escritorio sin bateria: estado valido, sin datos.
        return PowerStatus(available=False)

    secs = battery.secsleft
    if secs in (psutil.POWER_TIME_UNKNOWN, psutil.POWER_TIME_UNLIMITED):
        secs = None

    return PowerStatus(
        available=True,
        percent=battery.percent,
        plugged=battery.power_plugged,
        secs_left=secs,
    )


def list_serial_ports() -> list[SerialPortInfo]:
    """Deteccion dinamica de puertos serie e identificacion de medidores.

    Cada llamada re-enumera los puertos, de modo que conectar o
    desconectar un medidor USB se refleja en el siguiente refresh.
    """
    if not _SERIAL_AVAILABLE:
        return []
    ports: list[SerialPortInfo] = []
    try:
        for port in list_ports.comports():
            description = port.description or "desconocido"
            haystack = f"{description} {port.hwid or ''}".upper()
            meter = next(
                (model for key, model in KNOWN_METERS.items() if key in haystack),
                None,
            )
            ports.append(
                SerialPortInfo(
                    device=port.device, description=description, meter_model=meter
                )
            )
            if meter:
                logger.info("Medidor USB detectado: %s en %s", meter, port.device)
    except OSError as exc:
        logger.error("Error enumerando puertos serie: %s", exc)
    return ports


def check_alerts(
    previous: PowerStatus | None, current: PowerStatus, elapsed_seconds: float
) -> list[str]:
    """Sistema de alertas energeticas comparando dos lecturas consecutivas.

    Detecta:
        * bateria critica,
        * descarga anormalmente rapida (posible bateria danada o consumo
          inestable),
        * transiciones de carga (conexion/desconexion de alimentacion).
    """
    alerts: list[str] = []
    if not current.available or current.percent is None:
        return alerts

    if current.percent <= CRITICAL_BATTERY_PERCENT and not current.plugged:
        alerts.append(f"Bateria critica: {current.percent:.0f}%")

    if previous and previous.available and previous.percent is not None and elapsed_seconds > 0:
        delta = previous.percent - current.percent
        rate_per_min = delta * 60.0 / elapsed_seconds
        if rate_per_min > ABNORMAL_DISCHARGE_PCT_PER_MIN:
            alerts.append(
                f"Descarga anormal: {rate_per_min:.1f}%/min "
                f"(umbral {ABNORMAL_DISCHARGE_PCT_PER_MIN}%/min)"
            )
        if previous.plugged != current.plugged:
            alerts.append(
                "Alimentacion conectada" if current.plugged else "Alimentacion desconectada"
            )

    for alert in alerts:
        logger.warning("ALERTA energia: %s", alert)
    return alerts


# ----------------------------------------------------------------------
# Compatibilidad V1.0 (API basada en dicts, usada por codigo externo)
# ----------------------------------------------------------------------
def format_power_status(status: "PowerStatus | dict") -> str:
    """Texto legible; acepta PowerStatus o el dict legado de V1.0."""
    if isinstance(status, PowerStatus):
        return status.format()
    if not status.get("available"):
        return "Sin sensor de bateria (equipo de escritorio)"
    state = "Cargando (USB/AC)" if status.get("plugged") else "Descargando"
    text = f"{status['percent']:.0f}% - {state}"
    if status.get("secs_left"):
        text += f" - {status['secs_left'] // 60} min restantes"
    return text
