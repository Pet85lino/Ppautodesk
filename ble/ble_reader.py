"""
ble/ble_reader.py
-----------------
Lecturas GATT sobre dispositivos BLE conectados.

Funciones asincronas puras (sin Qt): las consume el motor de
ble_scanner.BLEEngine dentro de su event loop. Mantenerlas separadas
permite testearlas y reutilizarlas desde scripts de laboratorio.
"""

from __future__ import annotations

import logging

from bleak import BleakClient
from bleak.exc import BleakError

from ble.gatt_parser import BATTERY_LEVEL_CHAR_UUID, describe_services

logger = logging.getLogger("lino.ble.reader")


async def read_battery_level(client: BleakClient) -> int | None:
    """read_battery(): lee la caracteristica estandar Battery Level (0x2A19).

    Returns:
        Porcentaje 0-100, o None si el dispositivo no expone el servicio
        de bateria estandar (muchos TWS usan protocolos propietarios).
    """
    try:
        raw = await client.read_gatt_char(BATTERY_LEVEL_CHAR_UUID)
        level = int(raw[0]) if raw else None
        logger.info("Bateria de %s: %s%%", client.address, level)
        return level
    except BleakError as exc:
        logger.warning(
            "%s no expone Battery Service estandar (%s)", client.address, exc
        )
        return None


async def read_gatt_profile(client: BleakClient) -> list[dict]:
    """Inventario completo de servicios/caracteristicas del dispositivo.

    Base del modo reverse-engineering (V3): permite descubrir UUIDs
    propietarios donde algunos fabricantes publican bateria del case,
    codecs o telemetria extendida.
    """
    profile = describe_services(client.services)
    logger.info(
        "%s expone %d servicio(s) GATT", client.address, len(profile)
    )
    return profile
