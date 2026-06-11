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

# Propiedades PnP de Windows (claves DEVPKEY documentadas por la comunidad):
#   IsConnected -> conexion REAL actual (Status OK solo significa que el
#                  nodo PnP esta sano: los emparejados sin conectar
#                  tambien reportan OK, de ahi la clasificacion erronea).
#   Battery     -> porcentaje que Windows obtiene via HFP del auricular
#                  (el mismo 50% que muestra la pagina de Configuracion).
_PROP_IS_CONNECTED = "{83DA6326-97A6-4088-9453-A1923F573B29} 15"
_PROP_BATTERY = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"

_PS_COMMAND = (
    "$devs = Get-PnpDevice -Class Bluetooth | "
    "Where-Object { $_.InstanceId -match 'DEV_' }; "
    "$out = foreach ($d in $devs) { "
    f"$c = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName '{_PROP_IS_CONNECTED}' "
    "-ErrorAction SilentlyContinue).Data; "
    f"$b = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName '{_PROP_BATTERY}' "
    "-ErrorAction SilentlyContinue).Data; "
    "[PSCustomObject]@{ FriendlyName=$d.FriendlyName; Status=$d.Status; "
    "InstanceId=$d.InstanceId; IsConnected=$c; Battery=$b } }; "
    "ConvertTo-Json -InputObject @($out) -Compress"
)

_SUBPROCESS_TIMEOUT_S = 15


@dataclass
class SystemDevice:
    """Dispositivo Bluetooth registrado en el sistema operativo."""

    name: str
    mac: str                    # formato AA:BB:CC:DD:EE:FF
    connected: bool             # True = conectado AHORA (propiedad PnP)
    battery: int | None = None  # % reportado por Windows via HFP


def _format_mac(raw12: str) -> str:
    """AABBCCDDEEFF -> AA:BB:CC:DD:EE:FF."""
    raw12 = raw12.upper()
    return ":".join(raw12[i: i + 2] for i in range(0, 12, 2))


def parse_pnp_devices(json_text: str) -> list[SystemDevice]:
    """Interpreta la salida JSON de Get-PnpDevice (funcion pura, testeable).

    Reglas:
        * ConvertTo-Json devuelve un OBJETO (no lista) con un solo
          resultado: se normaliza a lista.
        * Cada fisico aparece varias veces (servicios hijos): se
          deduplica por MAC, prefiriendo el nombre de la entrada raiz.
        * Conexion: la propiedad IsConnected es la fuente de verdad
          (True/False). Solo si NINGUNA instancia la reporta se usa el
          fallback historico Status == OK (Windows antiguos).
        * Bateria: primer valor no nulo de la propiedad Battery (es el
          mismo porcentaje HFP que muestra Configuracion de Windows).
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

    # Acumuladores por MAC.
    info: dict[str, dict] = {}
    for entry in data:
        if not isinstance(entry, dict):
            continue
        instance_id = str(entry.get("InstanceId") or "")
        match = _DEV_MAC_RE.search(instance_id)
        if not match:
            continue
        mac = _format_mac(match.group(1))
        acc = info.setdefault(mac, {
            "name": None, "root_named": False,
            "is_connected": None, "status_ok": False, "battery": None,
        })

        name = str(entry.get("FriendlyName") or "").strip()
        is_root = instance_id.upper().startswith(("BTHENUM\\DEV_", "BTHLE\\DEV_"))
        if name and (acc["name"] is None or (is_root and not acc["root_named"])):
            acc["name"] = name
            acc["root_named"] = acc["root_named"] or is_root

        acc["status_ok"] = acc["status_ok"] or (
            str(entry.get("Status") or "").upper() == "OK"
        )

        raw_conn = entry.get("IsConnected")
        if raw_conn is not None:
            conn = str(raw_conn).strip().lower() in ("true", "1")
            acc["is_connected"] = bool(acc["is_connected"]) or conn

        raw_batt = entry.get("Battery")
        if acc["battery"] is None and raw_batt is not None:
            try:
                acc["battery"] = max(0, min(100, int(raw_batt)))
            except (TypeError, ValueError):
                pass

    devices: list[SystemDevice] = []
    for mac, acc in info.items():
        connected = (
            acc["is_connected"]
            if acc["is_connected"] is not None
            else acc["status_ok"]  # fallback para Windows sin la propiedad
        )
        devices.append(SystemDevice(
            name=acc["name"] or "(sin nombre)",
            mac=mac,
            connected=bool(connected),
            battery=acc["battery"],
        ))
    return devices


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
            "battery": sys_dev.battery,  # % HFP reportado por Windows
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
            "battery": None,
            "device": dev,
        })

    order = {STATUS_CONNECTED: 0, STATUS_PAIRED: 1, STATUS_NEARBY: 2}
    rows.sort(key=lambda r: (order[r["status"]], -(r["rssi"] or -999)))
    return rows
