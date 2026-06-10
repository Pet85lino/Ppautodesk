"""
usb/usb_monitor.py
------------------
Monitoreo energetico basico del equipo anfitrion (MVP).

En el MVP se reporta el estado de energia del sistema via psutil:
    * bateria del equipo (porcentaje, enchufado/descargando, tiempo restante)

En V2+ se agregara deteccion de carga USB del case mediante pyserial y
lectura de medidores USB externos (voltimetro/amperimetro USB).
"""

from __future__ import annotations

import logging

import psutil

logger = logging.getLogger("lino.usb.monitor")


def get_power_status() -> dict:
    """Estado de energia del sistema anfitrion.

    Returns:
        dict con claves:
            available (bool)      -> si hay sensor de bateria
            percent (float|None)  -> porcentaje de carga
            plugged (bool|None)   -> True si esta conectado a corriente/USB
            secs_left (int|None)  -> segundos de autonomia estimados
    """
    try:
        battery = psutil.sensors_battery()
    except (AttributeError, OSError) as exc:
        logger.warning("Sensor de bateria no accesible: %s", exc)
        battery = None

    if battery is None:
        # Equipos de escritorio sin bateria: estado valido, sin datos.
        return {"available": False, "percent": None, "plugged": None, "secs_left": None}

    secs = battery.secsleft
    if secs in (psutil.POWER_TIME_UNKNOWN, psutil.POWER_TIME_UNLIMITED):
        secs = None

    return {
        "available": True,
        "percent": battery.percent,
        "plugged": battery.power_plugged,
        "secs_left": secs,
    }


def format_power_status(status: dict) -> str:
    """Texto legible para el dashboard a partir de get_power_status()."""
    if not status["available"]:
        return "Sin sensor de bateria (equipo de escritorio)"

    state = "Cargando (USB/AC)" if status["plugged"] else "Descargando"
    text = f"{status['percent']:.0f}% - {state}"
    if status["secs_left"]:
        text += f" - {status['secs_left'] // 60} min restantes"
    return text
