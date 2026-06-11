"""
ble/system_devices.py
---------------------
Dispositivos Bluetooth registrados en el SISTEMA OPERATIVO (emparejados
y/o conectados), complementando el escaneo BLE.

Por que es necesario: los auriculares emparejados con Windows dejan de
anunciar BLE mientras estan inactivos o conectados via Bluetooth Classic
(A2DP/HFP), asi que NO aparecen en BleakScanner.discover(). La fuente de
verdad para "que esta conectado a este equipo" es el propio SO:

    * Windows -> PowerShell Get-PnpDevice -Class Bluetooth
                 (Status OK = presente/conectado; otro = emparejado).
    * Linux   -> bluetoothctl devices / devices Connected.
    * Otros   -> lista vacia (degradacion elegante).

La consulta a PowerShell tarda ~1-2 s: ejecutarla SIEMPRE desde un hilo
de trabajo, nunca en el hilo de la UI.
"""

from __future__ import annotations

import json
import logging
import re
import subprocess
import sys
from dataclasses import dataclass

logger = logging.getLogger("lino.ble.system")

# MAC embebida en el InstanceId de Windows: BTHENUM\DEV_AABBCCDDEEFF...
_DEV_MAC_RE = re.compile(r"DEV_([0-9A-F]{12})", re.IGNORECASE)

_PS_COMMAND = (
    "Get-PnpDevice -Class Bluetooth | "
    "Select-Object FriendlyName,Status,InstanceId | ConvertTo-Json -Compress"
)

_SUBPROCESS_TIMEOUT_S = 8


@dataclass
class SystemDevice:
    """Dispositivo Bluetooth registrado en el sistema operativo."""

    name: str
    mac: str          # formato AA:BB:CC:DD:EE:FF
    connected: bool   # True = presente/conectado ahora mismo


def _format_mac(raw12: str) -> str:
    """AABBCCDDEEFF -> AA:BB:CC:DD:EE:FF."""
    raw12 = raw12.upper()
    return ":".join(raw12[i: i + 2] for i in range(0, 12, 2))


def parse_pnp_devices(json_text: str) -> list[SystemDevice]:
    """Interpreta la salida JSON de Get-PnpDevice (funcion pura, testeable).

    Notas del formato:
        * ConvertTo-Json devuelve un OBJETO (no lista) si hay un solo
          dispositivo: se normaliza a lista.
        * Cada dispositivo fisico aparece varias veces (servicios hijos
          BTHENUM\\{uuid}...): se deduplica por MAC, marcando conectado
          si CUALQUIER instancia reporta Status OK y prefiriendo el
          nombre de la entrada raiz (BTHENUM\\DEV_... o BTHLE\\DEV_...).
    """
    try:
        data = json.loads(json_text)
    except json.JSONDecodeError as exc:
        logger.error("Salida de PowerShell no es JSON valido: %s", exc)
        return []
    if isinstance(data, dict):
        data = [data]
    if not isinstance(data, list):
        return []

    by_mac: dict[str, SystemDevice] = {}
    root_named: set[str] = set()
    for entry in data:
        if not isinstance(entry, dict):
            continue
        instance_id = str(entry.get("InstanceId") or "")
        match = _DEV_MAC_RE.search(instance_id)
        if not match:
            continue
        mac = _format_mac(match.group(1))
        name = str(entry.get("FriendlyName") or "(sin nombre)").strip()
        connected = str(entry.get("Status") or "").upper() == "OK"
        is_root = instance_id.upper().startswith(("BTHENUM\\DEV_", "BTHLE\\DEV_"))

        existing = by_mac.get(mac)
        if existing is None:
            by_mac[mac] = SystemDevice(name=name, mac=mac, connected=connected)
            if is_root:
                root_named.add(mac)
        else:
            existing.connected = existing.connected or connected
            # El nombre raiz es el legible ("MAXELL DYNAMC+"); los hijos
            # suelen llamarse "...Avrcp Transport" y similares.
            if is_root and mac not in root_named:
                existing.name = name
                root_named.add(mac)
    return list(by_mac.values())


def _list_windows() -> list[SystemDevice]:
    try:
        result = subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive", "-Command", _PS_COMMAND],
            capture_output=True,
            text=True,
            timeout=_SUBPROCESS_TIMEOUT_S,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        logger.error("PowerShell no disponible: %s", exc)
        return []
    if result.returncode != 0 or not result.stdout.strip():
        logger.warning("Get-PnpDevice fallo: %s", result.stderr.strip()[:200])
        return []
    return parse_pnp_devices(result.stdout)


def _list_linux() -> list[SystemDevice]:
    """bluetoothctl: emparejados + conectados (best effort)."""
    devices: dict[str, SystemDevice] = {}
    line_re = re.compile(r"^Device\s+([0-9A-F:]{17})\s+(.*)$", re.IGNORECASE)
    for args, connected in ((["devices"], False), (["devices", "Connected"], True)):
        try:
            result = subprocess.run(
                ["bluetoothctl", *args],
                capture_output=True, text=True, timeout=_SUBPROCESS_TIMEOUT_S,
            )
        except (OSError, subprocess.TimeoutExpired):
            return list(devices.values())
        for line in result.stdout.splitlines():
            match = line_re.match(line.strip())
            if not match:
                continue
            mac = match.group(1).upper()
            name = match.group(2).strip() or "(sin nombre)"
            if mac in devices:
                devices[mac].connected = devices[mac].connected or connected
            else:
                devices[mac] = SystemDevice(name=name, mac=mac, connected=connected)
    return list(devices.values())


def list_system_bluetooth_devices() -> list[SystemDevice]:
    """Dispositivos Bluetooth del SO. Llamar desde un hilo de trabajo."""
    if sys.platform == "win32":
        devices = _list_windows()
    elif sys.platform.startswith("linux"):
        devices = _list_linux()
    else:
        devices = []
    logger.info(
        "Dispositivos del sistema: %d (%d conectados)",
        len(devices), sum(1 for d in devices if d.connected),
    )
    return devices


# ======================================================================
# Fusion con el escaneo BLE (para la tabla del dashboard)
# ======================================================================
STATUS_CONNECTED = "connected"
STATUS_PAIRED = "paired"
STATUS_NEARBY = "nearby"


def merge_with_scan(system_devices: list[SystemDevice], scanned: list) -> list[dict]:
    """Combina dispositivos del SO con el ultimo escaneo BLE.

    Orden del resultado: conectados -> emparejados -> cercanos (BLE),
    y dentro de cada grupo por RSSI descendente cuando exista.

    Returns:
        Filas {mac, name, rssi, manufacturer, status, device} donde
        `device` es el DeviceInfo del escaneo si la MAC tambien anuncia
        BLE (None para dispositivos solo-sistema).
    """
    scanned_by_mac = {d.mac.upper(): d for d in scanned}
    rows: list[dict] = []
    seen: set[str] = set()

    for sys_dev in system_devices:
        mac = sys_dev.mac.upper()
        seen.add(mac)
        ble = scanned_by_mac.get(mac)
        rows.append({
            "mac": sys_dev.mac,
            "name": sys_dev.name if sys_dev.name != "(sin nombre)" or ble is None
                    else ble.name,
            "rssi": ble.rssi if ble else None,
            "manufacturer": ble.manufacturer if ble else None,
            "status": STATUS_CONNECTED if sys_dev.connected else STATUS_PAIRED,
            "device": ble,
        })

    for dev in scanned:
        if dev.mac.upper() in seen:
            continue
        rows.append({
            "mac": dev.mac,
            "name": dev.name,
            "rssi": dev.rssi,
            "manufacturer": dev.manufacturer,
            "status": STATUS_NEARBY,
            "device": dev,
        })

    order = {STATUS_CONNECTED: 0, STATUS_PAIRED: 1, STATUS_NEARBY: 2}
    rows.sort(key=lambda r: (order[r["status"]], -(r["rssi"] or -999)))
    return rows
