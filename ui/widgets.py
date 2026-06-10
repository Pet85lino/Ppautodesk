"""
ui/widgets.py
-------------
Componentes visuales reutilizables del dashboard.

    * GlassPanel       -> contenedor con efecto vidrio (glassmorphism).
    * BatteryIndicator -> barra de bateria con color segun nivel.
    * StatCard         -> tarjeta de metrica (titulo + valor grande).
    * LiveChart        -> grafica de linea en tiempo real (QPainter puro,
                          sin dependencias: RSSI live, bateria live...).
    * LogConsole       -> consola de logs integrada en la UI.
"""

from __future__ import annotations

import logging
from collections import deque

from PySide6.QtCore import QObject, QPointF, Qt, Signal
from PySide6.QtGui import QColor, QPainter, QPen, QPolygonF
from PySide6.QtWidgets import (
    QFrame,
    QLabel,
    QPlainTextEdit,
    QProgressBar,
    QVBoxLayout,
)

from ui.themes import battery_color


class GlassPanel(QFrame):
    """Panel translucido base de todas las secciones del dashboard."""

    def __init__(self, parent=None):
        super().__init__(parent)
        # El objectName enlaza con el selector QFrame#glassPanel del QSS.
        self.setObjectName("glassPanel")


class BatteryIndicator(QProgressBar):
    """Barra de bateria 0-100 % con color dinamico segun el nivel."""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setRange(0, 100)
        self.set_unknown()

    def set_level(self, level: int | None) -> None:
        """Actualiza nivel y color; None = bateria no disponible."""
        if level is None:
            self.set_unknown()
            return
        level = max(0, min(100, int(level)))
        self.setValue(level)
        self.setFormat(f"{level}%")
        # Color dinamico via stylesheet local (sobreescribe el chunk global).
        self.setStyleSheet(
            f"QProgressBar::chunk {{ background-color: {battery_color(level)};"
            f" border-radius: 7px; }}"
        )

    def set_unknown(self) -> None:
        """Estado 'sin datos': tipico de TWS con protocolo propietario."""
        self.setValue(0)
        self.setFormat("N/D")
        self.setStyleSheet(
            "QProgressBar::chunk { background-color: rgba(255,255,255,0.15);"
            " border-radius: 7px; }"
        )


class StatCard(GlassPanel):
    """Tarjeta de metrica rapida: titulo pequeno + valor destacado."""

    def __init__(self, title: str, value: str = "--", parent=None):
        super().__init__(parent)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 12, 16, 12)

        self._title = QLabel(title)
        self._title.setObjectName("mutedText")
        self._value = QLabel(value)
        self._value.setStyleSheet("font-size: 22px; font-weight: bold;")
        self._value.setAlignment(Qt.AlignmentFlag.AlignLeft)

        layout.addWidget(self._title)
        layout.addWidget(self._value)

    def set_value(self, value: str) -> None:
        self._value.setText(value)


class LiveChart(QFrame):
    """Grafica de linea en tiempo real, ligera (QPainter, sin matplotlib).

    Pensada para series que se actualizan cada pocos segundos: RSSI live,
    bateria live, latencia live. Mantiene una ventana deslizante de
    `max_points` muestras.
    """

    def __init__(self, title: str, unit: str = "", color: str = "#00E5FF",
                 max_points: int = 60, parent=None):
        super().__init__(parent)
        self.setObjectName("glassPanel")
        self.setMinimumHeight(110)
        self._title = title
        self._unit = unit
        self._color = QColor(color)
        self._points: deque[float] = deque(maxlen=max_points)

    def add_point(self, value: float) -> None:
        """Agrega una muestra y repinta (llamar desde el hilo de la UI)."""
        self._points.append(float(value))
        self.update()

    def set_series(self, values) -> None:
        """Reemplaza la serie completa (cambio de dispositivo seleccionado)."""
        self._points = deque(values, maxlen=self._points.maxlen)
        self.update()

    def clear(self) -> None:
        self._points.clear()
        self.update()

    def paintEvent(self, event) -> None:  # noqa: N802 (API Qt)
        super().paintEvent(event)
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        margin = 10
        w = self.width() - 2 * margin
        h = self.height() - 2 * margin - 16  # espacio para el titulo

        # Titulo + ultimo valor.
        painter.setPen(QColor("#7A8BA3"))
        last = f"{self._points[-1]:.0f}{self._unit}" if self._points else "--"
        painter.drawText(margin, margin + 10, f"{self._title}: {last}")

        if len(self._points) < 2 or w <= 0 or h <= 0:
            painter.end()
            return

        lo, hi = min(self._points), max(self._points)
        span = (hi - lo) or 1.0  # serie plana: evitar division por cero
        step = w / (self._points.maxlen - 1)

        polygon = QPolygonF()
        for i, value in enumerate(self._points):
            x = margin + i * step
            y = margin + 16 + h - ((value - lo) / span) * h
            polygon.append(QPointF(x, y))

        pen = QPen(self._color, 1.6)
        painter.setPen(pen)
        painter.drawPolyline(polygon)

        # Punto final destacado.
        painter.setBrush(self._color)
        painter.drawEllipse(polygon.last(), 3, 3)
        painter.end()


class _LogBridge(QObject):
    """Puente thread-safe: handlers de logging emiten desde cualquier hilo,
    la senal Qt entrega el texto en el hilo de la UI."""

    message = Signal(str)


class QtLogHandler(logging.Handler):
    """Handler de logging que reenvia los registros a la LogConsole."""

    def __init__(self, bridge: _LogBridge):
        super().__init__()
        self._bridge = bridge
        self.setFormatter(
            logging.Formatter("%(asctime)s | %(levelname)-7s | %(message)s", "%H:%M:%S")
        )

    def emit(self, record: logging.LogRecord) -> None:
        try:
            self._bridge.message.emit(self.format(record))
        except RuntimeError:
            # La UI ya fue destruida durante el cierre: ignorar.
            pass


class LogConsole(QPlainTextEdit):
    """Consola de solo lectura que muestra los logs 'lino.*' en vivo."""

    MAX_LINES = 500  # limite para no degradar la UI en sesiones largas

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setReadOnly(True)
        self.setMaximumBlockCount(self.MAX_LINES)

        self._bridge = _LogBridge()
        self._bridge.message.connect(self.appendPlainText)
        self._handler = QtLogHandler(self._bridge)
        logging.getLogger("lino").addHandler(self._handler)

    def detach(self) -> None:
        """Desconecta el handler al cerrar la ventana (evita fugas)."""
        logging.getLogger("lino").removeHandler(self._handler)
