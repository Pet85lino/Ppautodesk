"""
core/event_bus.py
-----------------
Bus de eventos publish/subscribe para desacoplar subsistemas.

Construido sobre senales Qt: la entrega es thread-safe (queued cuando el
publicador vive en otro hilo), por lo que el motor BLE, los hilos de audio
y la UI pueden comunicarse sin referencias directas entre si.

Topicos usados por la suite (convencion 'area.evento'):
    ble.scan        -> lista de DeviceInfo
    ble.battery     -> {mac, level}
    ble.event       -> {mac, type, detail}
    ble.fingerprint -> {mac, ...capacidades}
    power.status    -> PowerStatus
    audio.result    -> str
"""

from __future__ import annotations

import logging
from collections import defaultdict
from typing import Any, Callable

from PySide6.QtCore import QObject, Signal

logger = logging.getLogger("lino.core.bus")


class EventBus(QObject):
    """Bus pub/sub minimalista con entrega via senal Qt."""

    _published = Signal(str, object)  # topic, payload

    def __init__(self, parent: QObject | None = None):
        super().__init__(parent)
        self._subscribers: dict[str, list[Callable[[Any], None]]] = defaultdict(list)
        self._published.connect(self._dispatch)

    def publish(self, topic: str, payload: Any = None) -> None:
        """Publica un evento. Seguro desde cualquier hilo."""
        self._published.emit(topic, payload)

    def subscribe(self, topic: str, callback: Callable[[Any], None]) -> None:
        """Registra un callback para un topico exacto."""
        self._subscribers[topic].append(callback)

    def unsubscribe(self, topic: str, callback: Callable[[Any], None]) -> None:
        """Elimina un callback previamente registrado (ignora ausentes)."""
        try:
            self._subscribers[topic].remove(callback)
        except ValueError:
            pass

    def _dispatch(self, topic: str, payload: Any) -> None:
        """Entrega en el hilo del bus; un suscriptor roto no tumba al resto."""
        for callback in list(self._subscribers.get(topic, [])):
            try:
                callback(payload)
            except Exception as exc:  # noqa: BLE001 - frontera de despacho
                logger.error("Suscriptor de '%s' fallo: %s", topic, exc)
