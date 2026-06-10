#!/usr/bin/env python3
"""
LINO Audio Diagnostic - punto de entrada.

Suite de diagnostico tecnico para auriculares Bluetooth TWS:
escaneo BLE, lectura de bateria GATT, historial SQLite y dashboard
PySide6 con auto-refresh.

Uso:
    pip install -r requirements.txt
    python main.py
"""

from __future__ import annotations

import sys

from PySide6.QtWidgets import QApplication

from core.app_manager import AppManager
from ui.dashboard import MainWindow


def main() -> int:
    app_qt = QApplication(sys.argv)

    # Backend (config, logging, SQLite, motor BLE).
    manager = AppManager()
    manager.start()

    # Frontend (dashboard); el cierre de la ventana apaga el backend.
    window = MainWindow(manager)
    window.show()

    return app_qt.exec()


if __name__ == "__main__":
    sys.exit(main())
