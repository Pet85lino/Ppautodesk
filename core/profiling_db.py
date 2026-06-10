"""
core/profiling_db.py
--------------------
Mineria de patrones sobre los fingerprints acumulados.

Agrega los registros de device_capabilities en estadisticas de flota:
    * clusters de UUIDs (que servicios comparten los dispositivos),
    * patrones por fabricante (codecs tipicos, MTU tipico),
    * co-ocurrencia UUID propietario <-> fabricante.

Estos agregados son la materia prima de las heuristicas futuras
(clasificacion de chipset por ML): cuantos mas dispositivos se
fingerprinteen, mejores seran las inferencias.
"""

from __future__ import annotations

import logging
from collections import Counter, defaultdict

logger = logging.getLogger("lino.core.profiling")

# Base UUID Bluetooth SIG (los que NO terminan asi son propietarios).
SIG_UUID_SUFFIX = "-0000-1000-8000-00805f9b34fb"


def uuid_clusters(db) -> dict:
    """Frecuencia de cada UUID en la flota fingerprinteada.

    Returns:
        {"total_devices": N,
         "sig_uuids": {uuid: count},
         "proprietary_uuids": {uuid: count}}
    """
    fingerprints = db.all_fingerprints()
    sig: Counter = Counter()
    proprietary: Counter = Counter()

    for fp in fingerprints:
        seen: set[str] = set()
        for uuid in fp.get("uuids") or []:
            seen.add(uuid.lower())
        for service in fp.get("services") or []:
            if service.get("uuid"):
                seen.add(service["uuid"].lower())
        for uuid in seen:
            if uuid.endswith(SIG_UUID_SUFFIX):
                sig[uuid] += 1
            else:
                proprietary[uuid] += 1

    result = {
        "total_devices": len(fingerprints),
        "sig_uuids": dict(sig.most_common()),
        "proprietary_uuids": dict(proprietary.most_common()),
    }
    logger.info(
        "Clusters UUID: %d dispositivos, %d UUIDs SIG, %d propietarios",
        len(fingerprints), len(sig), len(proprietary),
    )
    return result


def manufacturer_patterns(db) -> dict:
    """Patrones por fabricante: codecs y MTU tipicos de la flota.

    El fabricante se toma del primer Company ID del manufacturer data.
    """
    fingerprints = db.all_fingerprints()
    by_vendor: dict[str, dict] = defaultdict(
        lambda: {"devices": 0, "codecs": Counter(), "mtus": []}
    )

    for fp in fingerprints:
        mfr_data = fp.get("manufacturer_data") or {}
        vendor = next(iter(mfr_data), "sin_company_id")
        entry = by_vendor[vendor]
        entry["devices"] += 1
        for codec in fp.get("codecs") or []:
            entry["codecs"][codec] += 1
        if fp.get("mtu"):
            entry["mtus"].append(fp["mtu"])

    result = {}
    for vendor, entry in by_vendor.items():
        mtus = entry["mtus"]
        result[vendor] = {
            "devices": entry["devices"],
            "common_codecs": [c for c, _ in entry["codecs"].most_common(4)],
            "typical_mtu": round(sum(mtus) / len(mtus)) if mtus else None,
        }
    logger.info("Patrones de fabricante: %d company IDs distintos", len(result))
    return result


def fleet_profile(db) -> dict:
    """Perfil completo de la flota (exportable a JSON para ML futuro)."""
    return {
        "uuid_clusters": uuid_clusters(db),
        "manufacturer_patterns": manufacturer_patterns(db),
    }
