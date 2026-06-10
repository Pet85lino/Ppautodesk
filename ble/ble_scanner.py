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

API publica (senales):
    scan_finished(list[DeviceInfo])  -> resultado de cada escaneo
    battery_read(str mac, object)    -> nivel de bateria (int 0-100 o None)
    device_connected(str mac, bool)  -> resultado de conexion
    engine_error(str)                -> errores legibles para la UI
"""

from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass, field
from datetime import datetime

from bleak import BleakClient, BleakScanner
from bleak.exc import BleakError
from PySide6.QtCore import QThread, Signal

from ble.ble_reader import read_battery_level
from ble.gatt_parser import manufacturer_from_adv

logger = logging.getLogger("lino.ble.scanner")


@dataclass
class DeviceInfo:
    """Snapshot de un dispositivo BLE detectado durante un escaneo."""

    mac: str
    name: str
    rssi: int
    manufacturer: str = "Desconocido"
    battery: int | None = None
    last_seen: str = field(
        default_factory=lambda: datetime.now().isoformat(timespec="seconds")
    )


class BLEEngine(QThread):
    """Hilo con event loop asyncio propio que ejecuta todas las operaciones BLE.

    La UI solicita trabajo con `request_scan()` / `request_battery()` y recibe
    los resultados via senales Qt. Nunca se debe llamar a metodos `_async_*`
    directamente desde el hilo principal.
    """

    scan_finished = Signal(list)          # list[DeviceInfo]
    battery_read = Signal(str, object)    # mac, int | None
    device_connected = Signal(str, bool)  # mac, exito
    engine_error = Signal(str)            # mensaje legible

    def __init__(self, scan_duration: float = 4.0, connect_timeout: float = 10.0):
        super().__init__()
        self._scan_duration = scan_duration
        self._connect_timeout = connect_timeout
        self._loop: asyncio.AbstractEventLoop | None = None
        self._scanning = False

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
        """Descubre dispositivos BLE cercanos con nombre, MAC y RSSI."""
        self._scanning = True
        try:
            logger.info("Escaneando BLE durante %.1f s...", self._scan_duration)
            found = await BleakScanner.discover(
                timeout=self._scan_duration, return_adv=True
            )
            devices: list[DeviceInfo] = []
            for device, adv in found.values():
                devices.append(
                    DeviceInfo(
                        mac=device.address,
                        name=device.name or "(sin nombre)",
                        rssi=adv.rssi if adv.rssi is not None else -127,
                        manufacturer=manufacturer_from_adv(adv.manufacturer_data),
                    )
                )
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

    async def _async_read_battery(self, mac: str) -> None:
        """Conexion GATT y lectura de la caracteristica Battery Level."""
        logger.info("Conectando a %s ...", mac)
        try:
            async with BleakClient(mac, timeout=self._connect_timeout) as client:
                self.device_connected.emit(mac, True)
                level = await read_battery_level(client)
                self.battery_read.emit(mac, level)
        except (BleakError, asyncio.TimeoutError, OSError) as exc:
            logger.warning("No se pudo conectar a %s: %s", mac, exc)
            self.device_connected.emit(mac, False)
            self.engine_error.emit(f"No se pudo conectar a {mac}: {exc}")
