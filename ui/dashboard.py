"""
ui/dashboard.py
---------------
Ventana principal de LINO Audio Diagnostic.

Secciones (panel lateral):
    * Dashboard -> escaneo BLE, tabla de dispositivos, bateria.
    * Audio     -> tonos de prueba L/R y test de latencia (MVP).
    * Energia   -> estado de carga USB/AC del equipo (psutil).
    * Logs      -> consola tecnica en vivo.

La ventana solo consume la API publica de AppManager (senales del motor
BLE + base de datos); no ejecuta operaciones Bluetooth directamente.
"""

from __future__ import annotations

import logging
import threading
from datetime import datetime

from PySide6.QtCore import Qt, QTimer
from PySide6.QtWidgets import (
    QButtonGroup,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QMainWindow,
    QPushButton,
    QStackedWidget,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from audio import audio_test, latency_test
from ble.ble_scanner import DeviceInfo
from core.app_manager import AppManager
from ui.themes import DARK_GLASS_QSS
from ui.widgets import BatteryIndicator, GlassPanel, LogConsole, StatCard
from usb.usb_monitor import format_power_status, get_power_status

logger = logging.getLogger("lino.ui.dashboard")

NAV_SECTIONS = ["Dashboard", "Audio", "Energia", "Logs"]


class MainWindow(QMainWindow):
    """Dashboard principal de la suite."""

    def __init__(self, app: AppManager):
        super().__init__()
        self.app = app
        self._battery_levels: dict[str, int | None] = {}  # cache mac -> nivel
        self._devices: list[DeviceInfo] = []

        self.setWindowTitle(
            f"{app.config.get('app_name')} v{app.config.get('version')}"
        )
        self.resize(
            int(app.config.get("ui.window_width", 1100)),
            int(app.config.get("ui.window_height", 680)),
        )
        self.setStyleSheet(DARK_GLASS_QSS)

        self._build_layout()
        self._connect_signals()

        # Auto-refresh: un escaneo BLE cada 5 s (configurable).
        self._refresh_timer = QTimer(self)
        self._refresh_timer.setInterval(
            int(app.config.get("scan.auto_refresh_ms", 5000))
        )
        self._refresh_timer.timeout.connect(self._on_auto_refresh)
        self._refresh_timer.start()

        # Primer escaneo inmediato al abrir la app.
        QTimer.singleShot(300, self.app.ble_engine.request_scan)

    # ==================================================================
    # Construccion de la interfaz
    # ==================================================================
    def _build_layout(self) -> None:
        root = QWidget()
        root_layout = QHBoxLayout(root)
        root_layout.setContentsMargins(0, 0, 0, 0)
        root_layout.setSpacing(0)

        # Las paginas deben existir antes que el sidebar (la navegacion
        # conecta directamente con self._pages.setCurrentIndex).
        self._pages = QStackedWidget()
        self._pages.addWidget(self._build_dashboard_page())
        self._pages.addWidget(self._build_audio_page())
        self._pages.addWidget(self._build_power_page())
        self._pages.addWidget(self._build_logs_page())

        root_layout.addWidget(self._build_sidebar())
        root_layout.addWidget(self._pages, stretch=1)

        self.setCentralWidget(root)
        self.statusBar().showMessage("Listo - esperando primer escaneo BLE")

    def _build_sidebar(self) -> QWidget:
        """Panel lateral de navegacion."""
        sidebar = GlassPanel()
        sidebar.setObjectName("sidebar")
        sidebar.setFixedWidth(190)

        layout = QVBoxLayout(sidebar)
        layout.setContentsMargins(12, 18, 12, 18)
        layout.setSpacing(6)

        title = QLabel("LINO AUDIO")
        title.setObjectName("appTitle")
        subtitle = QLabel("Diagnostic Suite")
        subtitle.setObjectName("mutedText")
        layout.addWidget(title)
        layout.addWidget(subtitle)
        layout.addSpacing(22)

        self._nav_group = QButtonGroup(self)
        self._nav_group.setExclusive(True)
        for index, section in enumerate(NAV_SECTIONS):
            btn = QPushButton(section)
            btn.setObjectName("navButton")
            btn.setCheckable(True)
            btn.setChecked(index == 0)
            btn.setCursor(Qt.CursorShape.PointingHandCursor)
            self._nav_group.addButton(btn, index)
            layout.addWidget(btn)
        self._nav_group.idClicked.connect(self._pages.setCurrentIndex)

        layout.addStretch()
        version = QLabel(f"v{self.app.config.get('version')} - MVP")
        version.setObjectName("mutedText")
        layout.addWidget(version)
        return sidebar

    def _build_dashboard_page(self) -> QWidget:
        """Pagina principal: metricas, tabla de dispositivos y bateria."""
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(18, 18, 18, 18)
        layout.setSpacing(14)

        # --- Fila de metricas rapidas ---
        cards = QHBoxLayout()
        self._card_found = StatCard("Dispositivos detectados", "0")
        self._card_known = StatCard(
            "Historial total", str(self.app.database.known_devices_count())
        )
        self._card_last_scan = StatCard("Ultimo escaneo", "--:--:--")
        for card in (self._card_found, self._card_known, self._card_last_scan):
            cards.addWidget(card)
        layout.addLayout(cards)

        # --- Tabla de dispositivos ---
        table_panel = GlassPanel()
        table_layout = QVBoxLayout(table_panel)
        table_layout.setContentsMargins(14, 14, 14, 14)

        header_row = QHBoxLayout()
        section = QLabel("Dispositivos BLE cercanos")
        section.setObjectName("sectionTitle")
        header_row.addWidget(section)
        header_row.addStretch()
        self._scan_button = QPushButton("Escanear ahora")
        self._scan_button.clicked.connect(self.app.ble_engine.request_scan)
        header_row.addWidget(self._scan_button)
        table_layout.addLayout(header_row)

        self._table = QTableWidget(0, 5)
        self._table.setHorizontalHeaderLabels(
            ["Nombre", "MAC", "RSSI (dBm)", "Fabricante", "Bateria"]
        )
        self._table.horizontalHeader().setSectionResizeMode(
            QHeaderView.ResizeMode.Stretch
        )
        self._table.verticalHeader().setVisible(False)
        self._table.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
        self._table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        self._table.itemSelectionChanged.connect(self._on_selection_changed)
        table_layout.addWidget(self._table)
        layout.addWidget(table_panel, stretch=1)

        # --- Panel de bateria del dispositivo seleccionado ---
        battery_panel = GlassPanel()
        battery_layout = QHBoxLayout(battery_panel)
        battery_layout.setContentsMargins(16, 12, 16, 12)

        self._selected_label = QLabel("Selecciona un dispositivo")
        self._selected_label.setObjectName("sectionTitle")
        battery_layout.addWidget(self._selected_label, stretch=1)

        self._battery_bar = BatteryIndicator()
        self._battery_bar.setFixedWidth(220)
        battery_layout.addWidget(self._battery_bar)

        self._battery_button = QPushButton("Leer bateria")
        self._battery_button.setEnabled(False)
        self._battery_button.clicked.connect(self._on_read_battery_clicked)
        battery_layout.addWidget(self._battery_button)
        layout.addWidget(battery_panel)

        return page

    def _build_audio_page(self) -> QWidget:
        """Tests de audio: tonos por canal y latencia por loopback."""
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(18, 18, 18, 18)

        panel = GlassPanel()
        panel_layout = QVBoxLayout(panel)
        panel_layout.setContentsMargins(16, 16, 16, 16)
        panel_layout.setSpacing(12)

        title = QLabel("Tests de audio")
        title.setObjectName("sectionTitle")
        panel_layout.addWidget(title)

        hint = QLabel(
            "Conecta los auriculares como salida de audio del sistema antes de "
            "ejecutar los tests. Los resultados aparecen en la seccion Logs."
        )
        hint.setObjectName("mutedText")
        hint.setWordWrap(True)
        panel_layout.addWidget(hint)

        buttons = QHBoxLayout()
        tests = [
            ("Tono izquierdo", lambda: audio_test.play_test_tone(channel="left")),
            ("Tono derecho", lambda: audio_test.play_test_tone(channel="right")),
            ("Balance L/R", audio_test.balance_test),
            ("Latencia", latency_test.estimate_latency),
        ]
        for label, func in tests:
            btn = QPushButton(label)
            btn.clicked.connect(lambda _=False, f=func: self._run_audio_test(f))
            buttons.addWidget(btn)
        panel_layout.addLayout(buttons)

        if not audio_test.audio_available():
            warning = QLabel("Subsistema de audio no disponible en este equipo.")
            warning.setStyleSheet("color: #FF2E97;")
            panel_layout.addWidget(warning)

        panel_layout.addStretch()
        layout.addWidget(panel)
        return page

    def _build_power_page(self) -> QWidget:
        """Estado energetico del equipo anfitrion (base del diagnostico USB)."""
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(18, 18, 18, 18)

        panel = GlassPanel()
        panel_layout = QVBoxLayout(panel)
        panel_layout.setContentsMargins(16, 16, 16, 16)

        title = QLabel("Energia del sistema")
        title.setObjectName("sectionTitle")
        panel_layout.addWidget(title)

        self._power_label = QLabel("Consultando...")
        panel_layout.addWidget(self._power_label)

        note = QLabel(
            "V2: deteccion de carga USB del case y voltaje estimado con "
            "medidores serie (pyserial)."
        )
        note.setObjectName("mutedText")
        note.setWordWrap(True)
        panel_layout.addWidget(note)
        panel_layout.addStretch()

        layout.addWidget(panel)
        return page

    def _build_logs_page(self) -> QWidget:
        """Consola tecnica en vivo (logs 'lino.*')."""
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(18, 18, 18, 18)

        title = QLabel("Logs tecnicos")
        title.setObjectName("sectionTitle")
        layout.addWidget(title)

        self._log_console = LogConsole()
        layout.addWidget(self._log_console)
        return page

    # ==================================================================
    # Senales del backend
    # ==================================================================
    def _connect_signals(self) -> None:
        engine = self.app.ble_engine
        engine.scan_finished.connect(self.update_dashboard)
        engine.battery_read.connect(self._on_battery_read)
        engine.device_connected.connect(self._on_device_connected)
        engine.engine_error.connect(self._on_engine_error)

    # ==================================================================
    # Logica de actualizacion
    # ==================================================================
    def update_dashboard(self, devices: list[DeviceInfo]) -> None:
        """update_dashboard(): refresca tabla y metricas tras cada escaneo."""
        self._devices = devices

        selected_mac = self._selected_mac()
        self._table.setRowCount(len(devices))
        for row, dev in enumerate(devices):
            battery = self._battery_levels.get(dev.mac)
            battery_text = f"{battery}%" if battery is not None else "--"
            values = [dev.name, dev.mac, str(dev.rssi), dev.manufacturer, battery_text]
            for col, value in enumerate(values):
                item = QTableWidgetItem(value)
                item.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
                self._table.setItem(row, col, item)
            # Mantener seleccion tras el auto-refresh.
            if dev.mac == selected_mac:
                self._table.selectRow(row)

        self._card_found.set_value(str(len(devices)))
        self._card_known.set_value(str(self.app.database.known_devices_count()))
        self._card_last_scan.set_value(datetime.now().strftime("%H:%M:%S"))
        self.statusBar().showMessage(
            f"Escaneo completado: {len(devices)} dispositivo(s) | "
            f"auto-refresh cada {self._refresh_timer.interval() // 1000} s"
        )

    def _on_auto_refresh(self) -> None:
        """Tick del temporizador: nuevo escaneo + estado de energia."""
        self.app.ble_engine.request_scan()
        self._power_label.setText(format_power_status(get_power_status()))

    def _selected_mac(self) -> str | None:
        row = self._table.currentRow()
        if 0 <= row < len(self._devices):
            return self._devices[row].mac
        return None

    def _on_selection_changed(self) -> None:
        mac = self._selected_mac()
        self._battery_button.setEnabled(mac is not None)
        if mac is None:
            self._selected_label.setText("Selecciona un dispositivo")
            self._battery_bar.set_unknown()
            return
        device = self._devices[self._table.currentRow()]
        self._selected_label.setText(f"{device.name}  ({device.mac})")
        self._battery_bar.set_level(self._battery_levels.get(mac))

    def _on_read_battery_clicked(self) -> None:
        mac = self._selected_mac()
        if mac:
            self._battery_button.setEnabled(False)
            self.statusBar().showMessage(f"Conectando a {mac} para leer bateria...")
            self.app.ble_engine.request_battery(mac)

    def _on_battery_read(self, mac: str, level) -> None:
        """Resultado de lectura de bateria (None = protocolo propietario)."""
        self._battery_levels[mac] = level
        self._battery_button.setEnabled(self._selected_mac() is not None)
        if mac == self._selected_mac():
            self._battery_bar.set_level(level)
        if level is None:
            self.statusBar().showMessage(
                f"{mac}: no expone bateria GATT estandar (protocolo propietario)"
            )
        else:
            self.statusBar().showMessage(f"Bateria de {mac}: {level}%")
        # Refrescar columna de bateria en la tabla.
        self.update_dashboard(self._devices)

    def _on_device_connected(self, mac: str, success: bool) -> None:
        if success:
            self.statusBar().showMessage(f"Conectado a {mac}, leyendo GATT...")

    def _on_engine_error(self, message: str) -> None:
        self._battery_button.setEnabled(self._selected_mac() is not None)
        self.statusBar().showMessage(message)

    # ==================================================================
    # Audio (en hilo aparte para no congelar la UI durante sd.wait)
    # ==================================================================
    def _run_audio_test(self, func) -> None:
        def worker():
            try:
                result = func()
                logger.info("Test de audio finalizado: resultado=%s", result)
            except Exception as exc:  # noqa: BLE001 - frontera de hilo
                logger.error("Fallo en test de audio: %s", exc)

        threading.Thread(target=worker, daemon=True).start()
        self.statusBar().showMessage("Ejecutando test de audio... (ver Logs)")

    # ==================================================================
    # Cierre ordenado
    # ==================================================================
    def closeEvent(self, event) -> None:
        self._refresh_timer.stop()
        self._log_console.detach()
        self.app.shutdown()
        event.accept()
