"""
ui/widgets.py
-------------
Componentes visuales reutilizables del dashboard.

    * GlassPanel       -> contenedor con efecto vidrio (glassmorphism).
    * BatteryIndicator -> barra de bateria con color segun nivel.
    * StatCard         -> tarjeta de metrica (titulo + valor grande).
    * LogConsole       -> consola de logs integrada en la UI.
"""

from __future__ import annotations

import logging

from PySide6.QtCore import Qt, Signal, QObject
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
