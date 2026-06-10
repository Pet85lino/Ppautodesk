"""
core/app_state.py
-----------------
Estado central en memoria de la aplicacion (fuente unica de verdad).

Mantiene los datos vivos que la UI grafica en tiempo real:
    * ultimo escaneo (lista de DeviceInfo),
    * cache de bateria por dispositivo,
    * historial RSSI en memoria (para las graficas live),
    * dispositivo actualmente seleccionado.

El estado se actualiza SOLO desde el hilo principal de Qt (las senales
del motor BLE entregan ahi); la UI lo lee directamente sin locks.
"""

from __future__ import annotations

from collections import defaultdict, deque

from PySide6.QtCore import QObject, Signal

# Muestras RSSI retenidas por dispositivo para las graficas live
# (60 muestras * 5 s de auto-refresh = 5 minutos de historia visible).
RSSI_HISTORY_LEN = 60


class AppState(QObject):
    """Estado observable compartido entre backend y UI."""

    devices_updated = Signal(list)         # nuevo escaneo aplicado
    battery_updated = Signal(str, object)  # mac, nivel (int | None)
    selection_changed = Signal(object)     # mac seleccionada (str | None)

    def __init__(self, parent: QObject | None = None):
        super().__init__(parent)
        self.devices: list = []                       # ultimo escaneo
        self.battery_levels: dict[str, int | None] = {}
        self.rssi_history: dict[str, deque] = defaultdict(
            lambda: deque(maxlen=RSSI_HISTORY_LEN)
        )
        self.battery_history_live: dict[str, deque] = defaultdict(
            lambda: deque(maxlen=RSSI_HISTORY_LEN)
        )
        self.selected_mac: str | None = None

    # ------------------------------------------------------------------
    # Mutadores (hilo principal unicamente)
    # ------------------------------------------------------------------
    def apply_scan(self, devices: list) -> None:
        """Integra un escaneo: lista viva + series RSSI para graficas."""
        self.devices = devices
        for dev in devices:
            self.rssi_history[dev.mac].append(dev.rssi)
        self.devices_updated.emit(devices)

    def set_battery(self, mac: str, level: int | None) -> None:
        """Actualiza cache y serie temporal de bateria de un dispositivo."""
        self.battery_levels[mac] = level
        if level is not None:
            self.battery_history_live[mac].append(level)
        self.battery_updated.emit(mac, level)

    def select(self, mac: str | None) -> None:
        if mac != self.selected_mac:
            self.selected_mac = mac
            self.selection_changed.emit(mac)

    # ------------------------------------------------------------------
    # Consultas
    # ------------------------------------------------------------------
    def device_by_mac(self, mac: str):
        """DeviceInfo del ultimo escaneo, o None si ya no esta visible."""
        return next((d for d in self.devices if d.mac == mac), None)

    def avg_rssi(self, mac: str) -> float | None:
        """RSSI promedio de la sesion (alimenta el fingerprinting)."""
        history = self.rssi_history.get(mac)
        if not history:
            return None
        return sum(history) / len(history)
