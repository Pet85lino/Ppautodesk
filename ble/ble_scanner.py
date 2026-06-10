"""
ble/ble_scanner.py
------------------
Motor Bluetooth BLE de la suite, basado en `bleak`.

Diseno:
    * `bleak` es asincrono (asyncio) y PySide6 tiene su propio event loop Qt.
      Para no bloquear la UI, este modulo ejecuta un event loop asyncio
      dentro de un QThread dedicado (BLEEngine).
    * La comunicacion hacia la UI se hace exclusivamente con senales Qt
      (thread-safe), de modo que el dashboard nunca toca asyncio.
    * Las lecturas GATT viven en ble_reader.py y la interpretacion de
      datos en gatt_parser.py (separacion motor / protocolo).

Capacidades (V1.2):
    * Escaneo con UUIDs anunciados y manufacturer data por dispositivo.
    * Fingerprinting GATT: servicios completos + MTU + RSSI promedio.
    * Eventos BLE: desconexiones, saltos de RSSI, dispositivos perdidos.

API publica (senales):
    scan_finished(list[DeviceInfo])     -> resultado de cada escaneo
    battery_read(str mac, object)       -> nivel de bateria (int | None)
    device_connected(str mac, bool)     -> resultado de conexion
    fingerprint_ready(str mac, dict)    -> capacidades GATT del dispositivo
    ble_event(str mac, str type, str)   -> eventos tecnicos (logging BLE)
    engine_error(str)                   -> errores legibles para la UI
"""

from __future__ import annotations

import asyncio
import logging
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime

from bleak import BleakClient, BleakScanner
from bleak.exc import BleakError
from PySide6.QtCore import QThread, Signal

from ble.ble_reader import read_battery_level, read_gatt_profile
from ble.gatt_parser import infer_codecs, manufacturer_from_adv

logger = logging.getLogger("lino.ble.scanner")

# Salto de RSSI entre escaneos consecutivos que se considera anomalo.
RSSI_JUMP_THRESHOLD_DB = 15

# Muestras RSSI retenidas por dispositivo para promedio de sesion.
RSSI_WINDOW = 20


@dataclass
class DeviceInfo:
    """Snapshot de un dispositivo BLE detectado durante un escaneo."""

    name: str
    mac: str
    rssi: int
    manufacturer: str | None = None
    uuids: list[str] = field(default_factory=list)
    battery: int | None = None
    last_seen: str = field(
        default_factory=lambda: datetime.now().isoformat(timespec="seconds")
    )


class BLEEngine(QThread):
    """Hilo con event loop asyncio propio que ejecuta todas las operaciones BLE.

    La UI solicita trabajo con `request_*()` y recibe los resultados via
    senales Qt. Nunca se debe llamar a metodos `_async_*` directamente
    desde el hilo principal.
    """

    scan_finished = Signal(list)            # list[DeviceInfo]
    battery_read = Signal(str, object)      # mac, int | None
    device_connected = Signal(str, bool)    # mac, exito
    fingerprint_ready = Signal(str, object) # mac, dict de capacidades
    ble_event = Signal(str, str, str)       # mac, tipo, detalle
    engine_error = Signal(str)              # mensaje legible

    def __init__(self, scan_duration: float = 4.0, connect_timeout: float = 10.0):
        super().__init__()
        self._scan_duration = scan_duration
        self._connect_timeout = connect_timeout
        self._loop: asyncio.AbstractEventLoop | None = None
        self._scanning = False

        # Estado de sesion para eventos y fingerprinting.
        self._rssi_history: dict[str, deque] = {}
        self._last_adv: dict[str, dict] = {}   # mac -> {manufacturer_hex, uuids, name}
        self._previous_macs: set[str] = set()

    # ------------------------------------------------------------------
    # Ciclo de vida del hilo
    # ------------------------------------------------------------------
    def run(self) -> None:
        """Punto de entrada del QThread: arranca el event loop asyncio."""
        self._loop = asyncio.new_event_loop()
        asyncio.set_event_loop(self._loop)
        logger.info("Motor BLE iniciado (loop asyncio en hilo dedicado)")
        try:
            self._loop.run_forever()
        finally:
            self._loop.close()
            logger.info("Motor BLE detenido")

    def shutdown(self) -> None:
        """Detiene el event loop y espera a que el hilo termine."""
        if self._loop and self._loop.is_running():
            self._loop.call_soon_threadsafe(self._loop.stop)
        self.wait(3000)

    # ------------------------------------------------------------------
    # API publica (thread-safe, llamada desde la UI)
    # ------------------------------------------------------------------
    def request_scan(self) -> None:
        """scan_devices(): solicita un escaneo BLE. Se ignora si ya hay uno
        en curso (evita solapamientos con el auto-refresh de 5 s)."""
        if self._scanning:
            logger.debug("Escaneo ya en curso, solicitud ignorada")
            return
        self._submit(self._async_scan())

    def request_battery(self, mac: str) -> None:
        """connect_device() + read_battery(): conecta a `mac` y lee bateria."""
        self._submit(self._async_read_battery(mac))

    def request_fingerprint(self, mac: str) -> None:
        """Fingerprinting completo: servicios GATT, MTU, bateria, codecs."""
        self._submit(self._async_fingerprint(mac))

    def session_avg_rssi(self, mac: str) -> float | None:
        """RSSI promedio observado en esta sesion (lectura simple,
        sin lock: el peor caso es un promedio una muestra desfasado)."""
        history = self._rssi_history.get(mac)
        if not history:
            return None
        return sum(history) / len(history)

    def _submit(self, coro) -> None:
        """Envia una corutina al loop asyncio desde el hilo Qt."""
        if self._loop is None or not self._loop.is_running():
            self.engine_error.emit("El motor BLE no esta iniciado")
            return
        asyncio.run_coroutine_threadsafe(coro, self._loop)

    # ------------------------------------------------------------------
    # Operaciones asincronas (se ejecutan dentro del loop del hilo BLE)
    # ------------------------------------------------------------------
    async def _async_scan(self) -> None:
        """Descubre dispositivos: nombre, MAC, RSSI, fabricante y UUIDs."""
        self._scanning = True
        try:
            logger.info("Escaneando BLE durante %.1f s...", self._scan_duration)
            found = await BleakScanner.discover(
                timeout=self._scan_duration, return_adv=True
            )
            devices: list[DeviceInfo] = []
            for device, adv in found.values():
                rssi = adv.rssi if adv.rssi is not None else -127
                uuids = list(adv.service_uuids or [])
                devices.append(
                    DeviceInfo(
                        name=device.name or "(sin nombre)",
                        mac=device.address,
                        rssi=rssi,
                        manufacturer=manufacturer_from_adv(adv.manufacturer_data),
                        uuids=uuids,
                    )
                )
                self._track_device(device.address, rssi, uuids, adv, device.name)

            self._detect_lost_devices({d.mac for d in devices})

            # Orden por intensidad de senal: los mas cercanos primero.
            devices.sort(key=lambda d: d.rssi, reverse=True)
            logger.info("Escaneo completado: %d dispositivo(s)", len(devices))
            self.scan_finished.emit(devices)
        except BleakError as exc:
            logger.error("Error de escaneo BLE: %s", exc)
            self.engine_error.emit(f"Error de escaneo BLE: {exc}")
        except OSError as exc:
            # Tipico cuando el adaptador Bluetooth esta apagado o ausente.
            logger.error("Adaptador Bluetooth no disponible: %s", exc)
            self.engine_error.emit(f"Adaptador Bluetooth no disponible: {exc}")
        finally:
            self._scanning = False

    def _track_device(self, mac: str, rssi: int, uuids: list[str], adv, name) -> None:
        """Actualiza historial RSSI/advertisement y emite eventos BLE."""
        history = self._rssi_history.setdefault(mac, deque(maxlen=RSSI_WINDOW))
        if history and abs(rssi - history[-1]) >= RSSI_JUMP_THRESHOLD_DB:
            self.ble_event.emit(
                mac, "rssi_jump", f"{history[-1]} dBm -> {rssi} dBm"
            )
        history.append(rssi)

        manufacturer_hex = {
            f"0x{cid:04X}": data.hex() for cid, data in adv.manufacturer_data.items()
        }
        self._last_adv[mac] = {
            "name": name or "(sin nombre)",
            "manufacturer_hex": manufacturer_hex,
            "uuids": uuids,
        }

    def _detect_lost_devices(self, current_macs: set[str]) -> None:
        """Dispositivos presentes en el escaneo anterior y ausentes ahora."""
        for mac in self._previous_macs - current_macs:
            self.ble_event.emit(mac, "out_of_range", "ausente en el ultimo escaneo")
        self._previous_macs = current_macs

    async def _async_read_battery(self, mac: str) -> None:
        """Conexion GATT y lectura de la caracteristica Battery Level."""
        logger.info("Conectando a %s ...", mac)
        try:
            async with BleakClient(
                mac,
                timeout=self._connect_timeout,
                disconnected_callback=self._on_disconnect,
            ) as client:
                self.device_connected.emit(mac, True)
                level = await read_battery_level(client)
                self.battery_read.emit(mac, level)
        except (BleakError, asyncio.TimeoutError, OSError) as exc:
            logger.warning("No se pudo conectar a %s: %s", mac, exc)
            self.device_connected.emit(mac, False)
            self.ble_event.emit(mac, "connect_failed", str(exc))
            self.engine_error.emit(f"No se pudo conectar a {mac}: {exc}")

    async def _async_fingerprint(self, mac: str) -> None:
        """Captura las capacidades del dispositivo en una sola conexion."""
        logger.info("Fingerprinting de %s ...", mac)
        try:
            async with BleakClient(
                mac,
                timeout=self._connect_timeout,
                disconnected_callback=self._on_disconnect,
            ) as client:
                self.device_connected.emit(mac, True)
                services = await read_gatt_profile(client)
                battery = await read_battery_level(client)
                try:
                    mtu = int(client.mtu_size)
                except (AttributeError, BleakError):
                    mtu = None

                adv = self._last_adv.get(mac, {})
                fingerprint = {
                    "mac": mac,
                    "name": adv.get("name", "(sin nombre)"),
                    "manufacturer_data": adv.get("manufacturer_hex", {}),
                    "uuids": adv.get("uuids", []),
                    "services": services,
                    "mtu": mtu,
                    "avg_rssi": self.session_avg_rssi(mac),
                    "battery": battery,
                    "codecs": infer_codecs(
                        self._manufacturer_for(mac), adv.get("uuids", [])
                    ),
                }
                self.battery_read.emit(mac, battery)
                self.fingerprint_ready.emit(mac, fingerprint)
                logger.info(
                    "Fingerprint de %s: %d servicio(s), MTU=%s",
                    mac, len(services), mtu,
                )
        except (BleakError, asyncio.TimeoutError, OSError) as exc:
            logger.warning("Fingerprinting de %s fallo: %s", mac, exc)
            self.device_connected.emit(mac, False)
            self.ble_event.emit(mac, "connect_failed", str(exc))
            self.engine_error.emit(f"Fingerprinting de {mac} fallo: {exc}")

    def _manufacturer_for(self, mac: str) -> str:
        """Fabricante del ultimo advertisement visto para esta MAC."""
        hex_map = self._last_adv.get(mac, {}).get("manufacturer_hex", {})
        if not hex_map:
            return "Desconocido"
        company_ids = {int(key, 16): b"" for key in hex_map}
        return manufacturer_from_adv(company_ids)

    def _on_disconnect(self, client: BleakClient) -> None:
        """Callback de bleak ante desconexion inesperada (hilo BLE)."""
        mac = getattr(client, "address", "desconocida")
        logger.warning("Desconexion BLE de %s", mac)
        self.ble_event.emit(mac, "disconnected", "desconexion del dispositivo")
