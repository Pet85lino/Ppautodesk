"""
ble/gatt_parser.py
------------------
Utilidades de interpretacion GATT y datos de advertisement BLE.

Centraliza:
    * UUIDs estandar del Bluetooth SIG usados por la suite.
    * Resolucion de fabricante via Company ID.
    * Descripcion de servicios/caracteristicas (base del modo
      reverse-engineering de V3).
"""

from __future__ import annotations

import logging

logger = logging.getLogger("lino.ble.gatt")

# ----------------------------------------------------------------------
# UUIDs GATT estandar (Bluetooth SIG)
# ----------------------------------------------------------------------
BATTERY_SERVICE_UUID = "0000180f-0000-1000-8000-00805f9b34fb"
BATTERY_LEVEL_CHAR_UUID = "00002a19-0000-1000-8000-00805f9b34fb"
DEVICE_INFO_SERVICE_UUID = "0000180a-0000-1000-8000-00805f9b34fb"
MODEL_NUMBER_CHAR_UUID = "00002a24-0000-1000-8000-00805f9b34fb"

# Nombres legibles de servicios conocidos (se ampliara progresivamente).
KNOWN_SERVICES: dict[str, str] = {
    BATTERY_SERVICE_UUID: "Battery Service",
    DEVICE_INFO_SERVICE_UUID: "Device Information",
    "00001800-0000-1000-8000-00805f9b34fb": "Generic Access",
    "00001801-0000-1000-8000-00805f9b34fb": "Generic Attribute",
    "0000110b-0000-1000-8000-00805f9b34fb": "A2DP Audio Sink",
    "0000111e-0000-1000-8000-00805f9b34fb": "Handsfree (HFP)",
    "0000110e-0000-1000-8000-00805f9b34fb": "AVRCP",
    "0000fe2c-0000-1000-8000-00805f9b34fb": "Google Fast Pair",
    "0000fd5a-0000-1000-8000-00805f9b34fb": "Samsung Galaxy Buds",
}

# ----------------------------------------------------------------------
# Company IDs (Bluetooth SIG) -> fabricante. Mapa minimo del MVP.
# ----------------------------------------------------------------------
COMPANY_IDS: dict[int, str] = {
    0x0001: "Ericsson",
    0x0006: "Microsoft",
    0x004C: "Apple",
    0x0075: "Samsung",
    0x00D7: "Qualcomm",
    0x0117: "Harman (JBL)",
    0x012D: "Sony",
    0x0157: "Xiaomi (Anhui Huami)",
    0x02D0: "Realtek",
    0x038F: "Xiaomi",
}


def manufacturer_from_adv(manufacturer_data: dict[int, bytes]) -> str:
    """Resuelve el fabricante a partir del Company ID del advertisement.

    Si el ID no esta en el mapa local se devuelve el codigo hexadecimal,
    util para identificarlo manualmente en bluetooth.com.
    """
    for company_id in manufacturer_data:
        name = COMPANY_IDS.get(company_id)
        return name if name else f"ID 0x{company_id:04X}"
    return "Desconocido"


# ----------------------------------------------------------------------
# Deteccion de codecs (heuristica probabilistica)
# ----------------------------------------------------------------------
# Los codecs A2DP se negocian a nivel de Bluetooth Classic y Windows no
# expone la negociacion via BLE, asi que la deteccion se infiere de
# fabricante + UUIDs anunciados. SBC es obligatorio en A2DP (siempre
# presente); el resto se marca como "probable" segun el ecosistema.
CODEC_RULES: list[tuple[str, list[str]]] = [
    ("Apple", ["AAC"]),
    ("Samsung", ["AAC", "Scalable (SSC)"]),
    ("Sony", ["AAC", "LDAC"]),
    ("Harman (JBL)", ["AAC"]),
    ("Qualcomm", ["aptX", "aptX HD"]),
    ("Xiaomi", ["AAC"]),
    ("Xiaomi (Anhui Huami)", ["AAC"]),
]

GOOGLE_FAST_PAIR_UUID = "0000fe2c-0000-1000-8000-00805f9b34fb"


def infer_codecs(manufacturer: str, uuids: list[str]) -> list[str]:
    """Codecs probables del dispositivo (heuristica, no negociacion real).

    SBC siempre esta presente (obligatorio en A2DP). El resto se infiere
    del fabricante y de UUIDs de servicios anunciados. Windows limita el
    acceso a la negociacion real del codec, por lo que el resultado se
    etiqueta como probable.
    """
    codecs = ["SBC"]
    for vendor, vendor_codecs in CODEC_RULES:
        if vendor.lower() in manufacturer.lower():
            codecs.extend(vendor_codecs)
            break

    lowered = [u.lower() for u in uuids]
    # Fast Pair => ecosistema Android moderno: AAC casi seguro, aptX comun.
    if GOOGLE_FAST_PAIR_UUID in lowered:
        for codec in ("AAC", "aptX"):
            if codec not in codecs:
                codecs.append(codec)
    return codecs


def service_name(uuid: str) -> str:
    """Nombre legible de un servicio GATT, o el UUID si es desconocido."""
    return KNOWN_SERVICES.get(uuid.lower(), uuid)


def describe_services(services) -> list[dict]:
    """Convierte la coleccion de servicios de un BleakClient en una
    estructura serializable (base de los logs exportables de V2/V3).

    Args:
        services: `client.services` de un BleakClient conectado.

    Returns:
        Lista de dicts: {uuid, nombre, caracteristicas: [{uuid, propiedades}]}
    """
    result: list[dict] = []
    try:
        for service in services:
            result.append(
                {
                    "uuid": service.uuid,
                    "name": service_name(service.uuid),
                    "characteristics": [
                        {"uuid": char.uuid, "properties": list(char.properties)}
                        for char in service.characteristics
                    ],
                }
            )
    except (AttributeError, TypeError) as exc:
        logger.warning("No se pudieron describir los servicios GATT: %s", exc)
    return result
