"""
ble/firmware_profiler.py
------------------------
Perfilado de firmware/chipset a partir de la huella BLE.

Combina fabricante, UUIDs anunciados y servicios GATT para inferir:
    * chipset probable (Qualcomm QCC, Airoha, Realtek, BES, Apple H-series),
    * ecosistema (Apple / Android-Fast Pair / generico),
    * cantidad de servicios propietarios (UUIDs 128-bit fuera de la base
      Bluetooth SIG), que delata firmware con telemetria extendida.

IMPORTANTE: el resultado es SPECULATIVO (heuristica sobre patrones
publicos); el chipset real solo se confirma con teardown o herramientas
del fabricante.
"""

from __future__ import annotations

import logging

logger = logging.getLogger("lino.ble.firmware")

# Base UUID Bluetooth SIG: 0000xxxx-0000-1000-8000-00805f9b34fb.
SIG_UUID_SUFFIX = "-0000-1000-8000-00805f9b34fb"

GOOGLE_FAST_PAIR_UUID = "0000fe2c-0000-1000-8000-00805f9b34fb"
SAMSUNG_BUDS_UUID = "0000fd5a-0000-1000-8000-00805f9b34fb"

# Reglas (fabricante -> chipset probable). Especulativas, basadas en
# plataformas tipicas de cada ecosistema TWS.
CHIPSET_BY_VENDOR: list[tuple[str, str]] = [
    ("apple", "Apple H1/H2 (plataforma propietaria)"),
    ("qualcomm", "Qualcomm QCC30xx/51xx"),
    ("samsung", "Broadcom/BES (plataforma Galaxy Buds)"),
    ("sony", "MediaTek/Airoha (plataforma Sony TWS)"),
    ("harman", "Qualcomm QCC (tipico en JBL)"),
    ("realtek", "Realtek RTL87xx"),
    ("xiaomi", "Airoha AB15xx / BES (tipico en TWS Xiaomi)"),
]


def profile_firmware(
    manufacturer: str | None,
    uuids: list[str] | None,
    services: list[dict] | None = None,
) -> dict:
    """Perfila el firmware probable de un dispositivo.

    Args:
        manufacturer: fabricante resuelto del advertisement.
        uuids: UUIDs anunciados.
        services: inventario GATT (formato de gatt_parser.describe_services).

    Returns:
        dict {probable_chipset, ecosystem, proprietary_services,
              signals: [evidencias], speculative: True}
    """
    manufacturer = manufacturer or "Desconocido"
    uuids = [u.lower() for u in (uuids or [])]
    services = services or []
    signals: list[str] = []

    # --- Chipset por fabricante ---
    chipset = "Desconocido"
    for vendor_key, vendor_chipset in CHIPSET_BY_VENDOR:
        if vendor_key in manufacturer.lower():
            chipset = vendor_chipset
            signals.append(f"fabricante: {manufacturer}")
            break

    # --- Ecosistema por UUIDs anunciados ---
    ecosystem = "generico"
    if "apple" in manufacturer.lower():
        ecosystem = "Apple"
    elif SAMSUNG_BUDS_UUID in uuids:
        ecosystem = "Samsung Galaxy"
        signals.append("UUID Galaxy Buds (0xFD5A)")
    elif GOOGLE_FAST_PAIR_UUID in uuids:
        ecosystem = "Android (Fast Pair)"
        signals.append("UUID Google Fast Pair (0xFE2C)")

    # --- Servicios propietarios en el inventario GATT ---
    all_uuids = uuids + [s.get("uuid", "").lower() for s in services]
    proprietary = sorted(
        {u for u in all_uuids if u and not u.endswith(SIG_UUID_SUFFIX)}
    )
    if proprietary:
        signals.append(f"{len(proprietary)} UUID(s) propietario(s)")

    result = {
        "probable_chipset": chipset,
        "ecosystem": ecosystem,
        "proprietary_services": proprietary,
        "signals": signals,
        "speculative": True,
    }
    logger.info(
        "Perfil firmware: chipset=%s, ecosistema=%s, propietarios=%d",
        chipset, ecosystem, len(proprietary),
    )
    return result


def profile_from_fingerprint(fingerprint: dict) -> dict:
    """Conveniencia: perfila desde un registro de device_capabilities."""
    manufacturer = None
    # El fabricante puede inferirse del primer company ID del payload.
    mfr_data = fingerprint.get("manufacturer_data") or {}
    if mfr_data:
        from ble.gatt_parser import COMPANY_IDS

        first_id = next(iter(mfr_data))
        try:
            manufacturer = COMPANY_IDS.get(int(first_id, 16))
        except (ValueError, TypeError):
            manufacturer = None
    return profile_firmware(
        manufacturer,
        fingerprint.get("uuids"),
        fingerprint.get("services"),
    )
