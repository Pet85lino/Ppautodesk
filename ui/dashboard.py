"""
ui/dashboard.py
---------------
Ventana principal de LINO Audio Diagnostic.

Secciones (panel lateral):
    * Dashboard -> escaneo BLE, tabla de dispositivos, bateria, export.
    * Audio     -> seleccion de salida, tonos, ruido, sweep, RMS,
                   latencia, jitter y sincronizacion L/R.
    * Energia   -> estado de carga del equipo, medidores USB detectados,
                   historial energetico con alertas.
    * Logs      -> consola tecnica en vivo.

La ventana solo consume la API publica de AppManager (senales del motor
BLE + base de datos); no ejecuta operaciones Bluetooth directamente.
Los tests de audio corren en hilos de trabajo y reportan via senales Qt.
"""

from __future__ import annotations

import logging
import threading
import time
from datetime import datetime

from PySide6.QtCore import Qt, QTimer, Signal
from PySide6.QtWidgets import (
    QButtonGroup,
    QComboBox,
    QGridLayout,
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

from audio import audio_test, latency_test, mic_profile
from ble.ble_scanner import DeviceInfo
from core.app_manager import AppManager
from core.config_manager import PROJECT_ROOT
from core.diagnostics import DiagnosticRunner
from ui.themes import DARK_GLASS_QSS
from ui.widgets import BatteryIndicator, GlassPanel, LiveChart, LogConsole, StatCard
from usb.usb_monitor import PowerStatus, check_alerts, get_power_status, list_serial_ports

logger = logging.getLogger("lino.ui.dashboard")

NAV_SECTIONS = ["Dashboard", "Audio", "Energia", "Laboratorio", "Logs"]
EXPORTS_DIR = PROJECT_ROOT / "exports"

# Persistir energia cada N ticks de auto-refresh (5 s * 12 = 1 min).
POWER_PERSIST_EVERY_TICKS = 12


class MainWindow(QMainWindow):
    """Dashboard principal de la suite."""

    # Senales emitidas desde hilos de trabajo de audio (entrega queued
    # en el hilo principal: seguro para tocar widgets y SQLite).
    audio_status = Signal(str)
    latency_measured = Signal(object)  # dict con la medicion para SQLite
    lab_status = Signal(str)           # resultados del modo laboratorio

    def __init__(self, app: AppManager):
        super().__init__()
        self.app = app
        self._battery_levels: dict[str, int | None] = {}  # cache mac -> nivel
        self._devices: list[DeviceInfo] = []
        self._last_power: PowerStatus | None = None
        self._last_power_time = time.monotonic()
        self._power_ticks = 0

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
        self._pages.addWidget(self._build_lab_page())
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
        version = QLabel(f"v{self.app.config.get('version')}")
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

        export_json_btn = QPushButton("Exportar JSON")
        export_json_btn.clicked.connect(self._on_export_json)
        header_row.addWidget(export_json_btn)

        export_csv_btn = QPushButton("Exportar CSV")
        export_csv_btn.clicked.connect(self._on_export_csv)
        header_row.addWidget(export_csv_btn)

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

        self._fingerprint_button = QPushButton("Fingerprint")
        self._fingerprint_button.setEnabled(False)
        self._fingerprint_button.setToolTip(
            "Captura servicios GATT, MTU, codecs probables y RSSI promedio"
        )
        self._fingerprint_button.clicked.connect(self._on_fingerprint_clicked)
        battery_layout.addWidget(self._fingerprint_button)

        self._diagnose_button = QPushButton("Diagnosticar auriculares")
        self._diagnose_button.setEnabled(False)
        self._diagnose_button.setToolTip(
            "Pipeline completo: bateria + RMS + latencia + jitter + reporte PDF"
        )
        self._diagnose_button.clicked.connect(self._on_diagnose_clicked)
        battery_layout.addWidget(self._diagnose_button)
        layout.addWidget(battery_panel)

        # --- Graficas en tiempo real del dispositivo seleccionado ---
        charts_row = QHBoxLayout()
        self._rssi_chart = LiveChart("RSSI live", unit=" dBm", color="#00E5FF")
        self._battery_chart = LiveChart("Bateria live", unit=" %", color="#2EE6A8")
        charts_row.addWidget(self._rssi_chart)
        charts_row.addWidget(self._battery_chart)
        layout.addLayout(charts_row)

        return page

    def _build_audio_page(self) -> QWidget:
        """Tests de audio: salida seleccionable, senales y mediciones DSP."""
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(18, 18, 18, 18)
        layout.setSpacing(14)

        # --- Seleccion de dispositivo de salida ---
        device_panel = GlassPanel()
        device_layout = QHBoxLayout(device_panel)
        device_layout.setContentsMargins(16, 12, 16, 12)

        device_label = QLabel("Salida de audio:")
        device_label.setObjectName("sectionTitle")
        device_layout.addWidget(device_label)

        self._output_combo = QComboBox()
        self._output_combo.setMinimumWidth(260)
        device_layout.addWidget(self._output_combo, stretch=1)

        mic_label = QLabel("Microfono:")
        mic_label.setObjectName("sectionTitle")
        device_layout.addWidget(mic_label)

        self._input_combo = QComboBox()
        self._input_combo.setMinimumWidth(260)
        device_layout.addWidget(self._input_combo, stretch=1)

        refresh_btn = QPushButton("Actualizar")
        refresh_btn.clicked.connect(self._refresh_audio_devices)
        device_layout.addWidget(refresh_btn)
        layout.addWidget(device_panel)

        self._refresh_audio_devices()
        self._output_combo.currentIndexChanged.connect(self._on_output_changed)
        self._input_combo.currentIndexChanged.connect(self._on_input_changed)

        # --- Botonera de tests ---
        panel = GlassPanel()
        panel_layout = QVBoxLayout(panel)
        panel_layout.setContentsMargins(16, 16, 16, 16)
        panel_layout.setSpacing(12)

        title = QLabel("Tests de audio")
        title.setObjectName("sectionTitle")
        panel_layout.addWidget(title)

        hint = QLabel(
            "Selecciona los auriculares como salida antes de ejecutar los "
            "tests. Volumen y duracion estan limitados por proteccion "
            "auditiva. Detalle completo en la seccion Logs."
        )
        hint.setObjectName("mutedText")
        hint.setWordWrap(True)
        panel_layout.addWidget(hint)

        grid = QGridLayout()
        grid.setSpacing(8)
        tests = [
            ("Tono izquierdo", self._test_tone_left),
            ("Tono derecho", self._test_tone_right),
            ("Balance L/R", self._test_balance),
            ("Ruido blanco", self._test_white_noise),
            ("Ruido rosa", self._test_pink_noise),
            ("Sweep 20Hz-20kHz", self._test_sweep),
            ("RMS L/R", self._test_rms),
            ("Latencia", self._test_latency),
            ("Jitter (5x)", self._test_jitter),
            ("Sync L/R", self._test_stereo_sync),
            ("Perfil de mic", self._test_mic_profile),
        ]
        for i, (label, func) in enumerate(tests):
            btn = QPushButton(label)
            btn.clicked.connect(lambda _=False, f=func: self._run_audio_test(f))
            grid.addWidget(btn, i // 5, i % 5)
        panel_layout.addLayout(grid)

        # --- Resultado del ultimo test ---
        self._audio_result = QLabel("Sin mediciones todavia")
        self._audio_result.setObjectName("sectionTitle")
        self._audio_result.setWordWrap(True)
        panel_layout.addWidget(self._audio_result)

        if not audio_test.audio_available():
            warning = QLabel("Subsistema de audio no disponible en este equipo.")
            warning.setStyleSheet("color: #FF2E97;")
            panel_layout.addWidget(warning)

        panel_layout.addStretch()
        layout.addWidget(panel, stretch=1)
        return page

    def _build_power_page(self) -> QWidget:
        """Energia del host + medidores USB detectados."""
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(18, 18, 18, 18)
        layout.setSpacing(14)

        panel = GlassPanel()
        panel_layout = QVBoxLayout(panel)
        panel_layout.setContentsMargins(16, 16, 16, 16)

        title = QLabel("Energia del sistema")
        title.setObjectName("sectionTitle")
        panel_layout.addWidget(title)

        self._power_label = QLabel("Consultando...")
        panel_layout.addWidget(self._power_label)

        meters_title = QLabel("Medidores USB / puertos serie")
        meters_title.setObjectName("sectionTitle")
        panel_layout.addWidget(meters_title)

        self._meters_label = QLabel("Buscando puertos serie...")
        self._meters_label.setObjectName("mutedText")
        self._meters_label.setWordWrap(True)
        panel_layout.addWidget(self._meters_label)

        note = QLabel(
            "El historial energetico se guarda en SQLite cada minuto. "
            "V2: lectura de voltaje/corriente/mAh de medidores UM25C, "
            "FNB58, TC66C y AT34."
        )
        note.setObjectName("mutedText")
        note.setWordWrap(True)
        panel_layout.addWidget(note)
        panel_layout.addStretch()

        layout.addWidget(panel)
        return page

    def _build_lab_page(self) -> QWidget:
        """Modo laboratorio: dropouts, espectro, raw BLE, score y comparacion."""
        from PySide6.QtWidgets import QPlainTextEdit

        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setContentsMargins(18, 18, 18, 18)
        layout.setSpacing(14)

        panel = GlassPanel()
        panel_layout = QVBoxLayout(panel)
        panel_layout.setContentsMargins(16, 16, 16, 16)
        panel_layout.setSpacing(10)

        title = QLabel("Modo laboratorio")
        title.setObjectName("sectionTitle")
        panel_layout.addWidget(title)

        # --- Fila 1: pruebas instrumentales ---
        row1 = QHBoxLayout()
        tools = [
            ("Dropouts (5s)", self._lab_dropout),
            ("Espectro (mic 3s)", self._lab_spectrum),
            ("Raw BLE log (10s)", self._lab_raw_log),
            ("Score dispositivo", self._lab_score),
            ("Tendencia temporal", self._lab_trend),
        ]
        for label, handler in tools:
            btn = QPushButton(label)
            btn.clicked.connect(handler)
            row1.addWidget(btn)
        panel_layout.addLayout(row1)

        # --- Fila 2: comparacion A/B ---
        row2 = QHBoxLayout()
        row2.addWidget(QLabel("Comparar:"))
        self._cmp_combo_a = QComboBox()
        self._cmp_combo_b = QComboBox()
        for combo in (self._cmp_combo_a, self._cmp_combo_b):
            combo.setMinimumWidth(220)
            row2.addWidget(combo)
        refresh_btn = QPushButton("Actualizar lista")
        refresh_btn.clicked.connect(self._refresh_compare_combos)
        row2.addWidget(refresh_btn)
        compare_btn = QPushButton("Comparar A vs B")
        compare_btn.clicked.connect(self._lab_compare)
        row2.addWidget(compare_btn)
        row2.addStretch()
        panel_layout.addLayout(row2)

        # --- Salida de resultados ---
        self._lab_output = QPlainTextEdit()
        self._lab_output.setReadOnly(True)
        self._lab_output.setPlaceholderText(
            "Los resultados del laboratorio apareceran aqui..."
        )
        panel_layout.addWidget(self._lab_output, stretch=1)

        layout.addWidget(panel, stretch=1)
        self._refresh_compare_combos()
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
        engine.fingerprint_ready.connect(self._on_fingerprint_ready)
        engine.raw_log_ready.connect(self._on_raw_log_ready)
        engine.ble_event.connect(self._on_ble_event)
        engine.engine_error.connect(self._on_engine_error)

        # Resultados de tests de audio (desde hilos de trabajo).
        self.audio_status.connect(self._on_audio_status)
        self.latency_measured.connect(self._on_latency_measured)
        self.lab_status.connect(self._append_lab_output)

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

        # Grafica RSSI live del dispositivo seleccionado (serie en AppState).
        if selected_mac:
            self._rssi_chart.set_series(self.app.state.rssi_history.get(selected_mac, []))
        self.statusBar().showMessage(
            f"Escaneo completado: {len(devices)} dispositivo(s) | "
            f"auto-refresh cada {self._refresh_timer.interval() // 1000} s"
        )

    def _on_auto_refresh(self) -> None:
        """Tick del temporizador: nuevo escaneo + estado de energia."""
        self.app.ble_engine.request_scan()
        self._update_power()

    def _update_power(self) -> None:
        """Refresca energia, evalua alertas y persiste cada minuto."""
        now = time.monotonic()
        status = get_power_status()
        self._power_label.setText(status.format())

        alerts = check_alerts(self._last_power, status, now - self._last_power_time)
        for alert in alerts:
            self.app.database.save_log("ALERTA", alert)
            self.statusBar().showMessage(f"ALERTA energia: {alert}")

        self._power_ticks += 1
        if self._power_ticks % POWER_PERSIST_EVERY_TICKS == 1:  # primer tick y cada minuto
            self.app.database.save_power(status.percent, status.plugged)

        ports = list_serial_ports()
        if ports:
            self._meters_label.setText("\n".join(p.format() for p in ports))
        else:
            self._meters_label.setText("Sin puertos serie detectados")

        self._last_power = status
        self._last_power_time = now

    # ==================================================================
    # Exportaciones
    # ==================================================================
    def _on_export_json(self) -> None:
        path = self.app.database.export_json(EXPORTS_DIR)
        self.statusBar().showMessage(
            f"Exportado: {path}" if path else "Error en exportacion JSON (ver Logs)"
        )

    def _on_export_csv(self) -> None:
        paths = self.app.database.export_csv(EXPORTS_DIR)
        self.statusBar().showMessage(
            f"Exportados {len(paths)} CSV en {EXPORTS_DIR}"
            if paths
            else "Error en exportacion CSV (ver Logs)"
        )

    # ==================================================================
    # Seleccion y bateria BLE
    # ==================================================================
    def _selected_mac(self) -> str | None:
        row = self._table.currentRow()
        if 0 <= row < len(self._devices):
            return self._devices[row].mac
        return None

    def _on_selection_changed(self) -> None:
        mac = self._selected_mac()
        self.app.state.select(mac)
        for btn in (self._battery_button, self._fingerprint_button, self._diagnose_button):
            btn.setEnabled(mac is not None)
        if mac is None:
            self._selected_label.setText("Selecciona un dispositivo")
            self._battery_bar.set_unknown()
            self._rssi_chart.clear()
            self._battery_chart.clear()
            return
        device = self._devices[self._table.currentRow()]
        self._selected_label.setText(f"{device.name}  ({device.mac})")
        self._battery_bar.set_level(self._battery_levels.get(mac))
        # Cargar las series live acumuladas en AppState para esta MAC.
        self._rssi_chart.set_series(self.app.state.rssi_history.get(mac, []))
        self._battery_chart.set_series(self.app.state.battery_history_live.get(mac, []))

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
            if level is not None:
                self._battery_chart.add_point(level)
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

    # ==================================================================
    # Fingerprinting y diagnostico automatico
    # ==================================================================
    def _on_fingerprint_clicked(self) -> None:
        mac = self._selected_mac()
        if mac:
            self._fingerprint_button.setEnabled(False)
            self.statusBar().showMessage(f"Capturando fingerprint de {mac}...")
            self.app.ble_engine.request_fingerprint(mac)

    def _on_fingerprint_ready(self, mac: str, fingerprint: dict) -> None:
        self._fingerprint_button.setEnabled(self._selected_mac() is not None)
        codecs = ", ".join(fingerprint.get("codecs", [])) or "desconocidos"
        services = len(fingerprint.get("services", []))
        mtu = fingerprint.get("mtu") or "--"
        self.statusBar().showMessage(
            f"Fingerprint {mac}: {services} servicios GATT, MTU {mtu}, "
            f"codecs probables: {codecs}"
        )

    def _on_diagnose_clicked(self) -> None:
        mac = self._selected_mac()
        device = self.app.state.device_by_mac(mac) if mac else None
        if device is None:
            self.statusBar().showMessage("El dispositivo ya no esta visible")
            return
        self._diagnose_button.setEnabled(False)
        # Referencia viva en self: evita que Qt recoja el runner a mitad.
        self._diag_runner = DiagnosticRunner(self.app, device, parent=self)
        self._diag_runner.progress.connect(self.statusBar().showMessage)
        self._diag_runner.finished.connect(self._on_diagnose_finished)
        self._diag_runner.start()

    def _on_diagnose_finished(self, report: dict) -> None:
        self._diagnose_button.setEnabled(self._selected_mac() is not None)
        path = report.get("report_path")
        if path:
            self.statusBar().showMessage(f"Diagnostico completado - reporte: {path}")
        else:
            self.statusBar().showMessage(
                "Diagnostico completado (no se pudo generar el reporte, ver Logs)"
            )
        logger.info("Diagnostico finalizado para %s", report.get("device", {}).get("mac"))

    def _on_engine_error(self, message: str) -> None:
        self._battery_button.setEnabled(self._selected_mac() is not None)
        self.statusBar().showMessage(message)

    def _on_ble_event(self, mac: str, event_type: str, detail: str) -> None:
        """Reaccion al watchdog: escalar el intervalo de escaneo cuando el
        adaptador esta degradado y restaurarlo al recuperarse."""
        if event_type == "adapter_watchdog":
            slowed = min(60_000, self._refresh_timer.interval() * 2)
            self._refresh_timer.setInterval(slowed)
            self.statusBar().showMessage(
                f"Adaptador BLE degradado ({detail}); auto-refresh a {slowed // 1000} s"
            )
        elif event_type == "adapter_recovered":
            configured = int(self.app.config.get("scan.auto_refresh_ms", 5000))
            self._refresh_timer.setInterval(configured)
            self.statusBar().showMessage(
                f"Adaptador BLE recuperado; auto-refresh a {configured // 1000} s"
            )

    # ==================================================================
    # Audio: seleccion de salida
    # ==================================================================
    def _refresh_audio_devices(self) -> None:
        """Rellena los combos: solo salidas en uno, solo entradas en otro."""
        self._output_combo.blockSignals(True)
        self._output_combo.clear()
        self._output_combo.addItem("(Salida por defecto del sistema)", None)
        for dev in audio_test.list_output_devices():
            self._output_combo.addItem(
                f"[{dev['index']}] {dev['name']} ({dev['outputs']} ch)", dev["index"]
            )
        self._output_combo.blockSignals(False)

        self._input_combo.blockSignals(True)
        self._input_combo.clear()
        self._input_combo.addItem("(Entrada por defecto del sistema)", None)
        for dev in mic_profile.list_input_devices():
            self._input_combo.addItem(
                f"[{dev['index']}] {dev['name']} ({dev['inputs']} ch)", dev["index"]
            )
        self._input_combo.blockSignals(False)

    def _on_output_changed(self) -> None:
        index = self._output_combo.currentData()
        if audio_test.set_output_device(index):
            self.statusBar().showMessage(
                f"Salida de audio: {self._output_combo.currentText()}"
            )
        else:
            self.statusBar().showMessage("Dispositivo de salida invalido")

    def _on_input_changed(self) -> None:
        index = self._input_combo.currentData()
        if mic_profile.set_input_device(index):
            self.statusBar().showMessage(
                f"Microfono: {self._input_combo.currentText()}"
            )
        else:
            self.statusBar().showMessage("Microfono invalido")

    # ==================================================================
    # Audio: tests (cada funcion devuelve un texto de resultado)
    # ==================================================================
    def _test_tone_left(self) -> str:
        ok = audio_test.play_test_tone(channel="left")
        return "Tono izquierdo reproducido" if ok else "Fallo el tono izquierdo"

    def _test_tone_right(self) -> str:
        ok = audio_test.play_test_tone(channel="right")
        return "Tono derecho reproducido" if ok else "Fallo el tono derecho"

    def _test_balance(self) -> str:
        ok = audio_test.balance_test()
        return "Balance L/R completado" if ok else "Fallo el test de balance"

    def _test_white_noise(self) -> str:
        ok = audio_test.play_white_noise()
        return "Ruido blanco reproducido" if ok else "Fallo el ruido blanco"

    def _test_pink_noise(self) -> str:
        ok = audio_test.play_pink_noise()
        return "Ruido rosa reproducido" if ok else "Fallo el ruido rosa"

    def _test_sweep(self) -> str:
        ok = audio_test.play_sweep()
        return "Sweep 20 Hz - 20 kHz completado" if ok else "Fallo el sweep"

    def _test_rms(self) -> str:
        result = audio_test.measure_rms_balance()
        if result is None:
            return "RMS L/R: medicion no disponible (revisar microfono)"
        return (
            f"RMS L={result['rms_left']:.4f}  R={result['rms_right']:.4f}  "
            f"diferencia={result['diff_db']:+.1f} dB"
        )

    def _test_latency(self) -> str:
        result = latency_test.measure_once()
        if result is None:
            return "Latencia: medicion no disponible"
        if result.valid:
            self.latency_measured.emit(
                {
                    "latency_ms": result.latency_ms,
                    "jitter_ms": None,
                    "confidence": result.confidence,
                    "channel": result.channel,
                    "classification": result.classification,
                }
            )
        return (
            f"Latencia: {result.latency_ms:.1f} ms ({result.classification}, "
            f"confianza {result.confidence:.2f})"
            + ("" if result.valid else " - DESCARTADA por baja confianza")
        )

    def _test_jitter(self) -> str:
        result = latency_test.jitter_test(runs=5)
        if result is None:
            return "Jitter: medicion no disponible"
        self.latency_measured.emit(
            {
                "latency_ms": result.mean_ms,
                "jitter_ms": result.std_ms,
                "confidence": None,
                "channel": "both",
                "classification": result.classification,
            }
        )
        return (
            f"Jitter: media {result.mean_ms:.1f} ms, sigma {result.std_ms:.1f} ms "
            f"({result.valid_runs}/{result.runs} validas, {result.classification})"
        )

    def _test_mic_profile(self) -> str:
        """Perfil del microfono uplink. Mantener ruido ambiente constante
        (ventilador o ruido por parlantes) durante los 4 s de captura."""
        chipset = None
        mac = self.app.state.selected_mac
        if mac:
            caps = self.app.database.get_fingerprint(mac)
            if caps:
                from ble.firmware_profiler import profile_from_fingerprint

                chipset = profile_from_fingerprint(caps).get("probable_chipset")
        profile = mic_profile.microphone_profile(probable_chipset=chipset)
        if profile is None:
            return "Perfil de mic: entrada de audio no disponible"
        return mic_profile.format_mic_profile(profile)

    def _test_stereo_sync(self) -> str:
        result = latency_test.stereo_sync_test()
        if result is None:
            return "Sync L/R: medicion no disponible"
        return (
            f"Sync L/R: L={result['left_ms']:.1f} ms, R={result['right_ms']:.1f} ms, "
            f"drift={result['drift_ms']:+.1f} ms"
        )

    def _run_audio_test(self, func) -> None:
        """Ejecuta un test en hilo aparte (sd.wait es bloqueante)."""

        def worker():
            try:
                message = func()
            except Exception as exc:  # noqa: BLE001 - frontera de hilo
                logger.error("Fallo en test de audio: %s", exc)
                message = f"Fallo en test de audio: {exc}"
            self.audio_status.emit(message)

        threading.Thread(target=worker, daemon=True).start()
        self.statusBar().showMessage("Ejecutando test de audio...")

    def _on_audio_status(self, message: str) -> None:
        """Resultado de un test (entregado en el hilo principal)."""
        self._audio_result.setText(message)
        self.statusBar().showMessage(message)
        logger.info("Test de audio: %s", message)

    def _on_latency_measured(self, data: dict) -> None:
        """Persiste mediciones de latencia/jitter (hilo principal)."""
        self.app.database.save_latency(
            latency_ms=data["latency_ms"],
            jitter_ms=data["jitter_ms"],
            confidence=data["confidence"],
            channel=data["channel"],
            classification=data["classification"],
        )

    # ==================================================================
    # Modo laboratorio
    # ==================================================================
    def _append_lab_output(self, text: str) -> None:
        self._lab_output.appendPlainText(text)

    def _run_lab_test(self, func) -> None:
        """Test de laboratorio en hilo aparte; resultado a la consola lab."""

        def worker():
            try:
                message = func()
            except Exception as exc:  # noqa: BLE001 - frontera de hilo
                logger.error("Fallo en test de laboratorio: %s", exc)
                message = f"Fallo: {exc}"
            self.lab_status.emit(message)

        threading.Thread(target=worker, daemon=True).start()
        self._append_lab_output(">> Ejecutando...")

    def _lab_dropout(self) -> None:
        from audio import dropout_test

        def run() -> str:
            result = dropout_test.dropout_test(duration=5.0)
            if result is None:
                return "Dropouts: medicion no disponible (revisar audio/microfono)"
            return f"Continuidad de audio: {result.format()}"

        self._run_lab_test(run)

    def _lab_spectrum(self) -> None:
        from audio import spectrum

        def run() -> str:
            path = spectrum.capture_and_plot(duration=3.0, out_dir=EXPORTS_DIR)
            return (
                f"Analisis espectral guardado: {path}"
                if path
                else "Espectro: captura no disponible"
            )

        self._run_lab_test(run)

    def _lab_raw_log(self) -> None:
        self._append_lab_output(">> Capturando advertisements BLE (10 s)...")
        self.app.ble_engine.request_raw_log(10.0)

    def _on_raw_log_ready(self, raw_log: dict) -> None:
        lines = [
            f"Raw BLE log: {raw_log['total_packets']} paquetes de "
            f"{raw_log['devices']} dispositivo(s) en {raw_log['duration_s']:.0f} s"
        ]
        for mac, info in sorted(raw_log["intervals"].items()):
            avg = info["avg_interval_ms"]
            lines.append(
                f"  {mac}: {info['packets']} pkt, intervalo medio "
                f"{f'{avg:.0f} ms' if avg else '--'}"
            )
        self._append_lab_output("\n".join(lines))

    def _lab_score(self) -> None:
        mac = self._selected_mac() or self.app.state.selected_mac
        if not mac:
            self._append_lab_output("Score: selecciona un dispositivo en el Dashboard")
            return
        from core import scoring

        result = scoring.device_quality_score(self.app.database, mac)
        self._append_lab_output(
            f"Device Quality Score de {mac}:\n{scoring.format_score(result)}"
        )

    def _lab_trend(self) -> None:
        mac = self._selected_mac() or self.app.state.selected_mac
        if not mac:
            self._append_lab_output("Tendencia: selecciona un dispositivo en el Dashboard")
            return
        from core.comparison import compare_over_time

        result = compare_over_time(self.app.database, mac)
        if result is None:
            self._append_lab_output(
                f"Tendencia de {mac}: historico de bateria insuficiente (<8 lecturas)"
            )
            return
        verdict = "DEGRADANDOSE" if result["degrading"] else "estable"
        self._append_lab_output(
            f"Tendencia de {mac} ({result['samples']} lecturas):\n"
            f"  pico antiguo {result['old']['peak']}% -> reciente "
            f"{result['recent']['peak']}% ({result['peak_trend_pct']:+.1f}%, {verdict})"
        )

    def _refresh_compare_combos(self) -> None:
        """Rellena los combos A/B con el catalogo historico de dispositivos."""
        rows = self.app.database.devices_catalog(limit=50)
        for combo in (self._cmp_combo_a, self._cmp_combo_b):
            combo.clear()
            for mac, name in rows:
                combo.addItem(f"{name} ({mac})", mac)

    def _lab_compare(self) -> None:
        mac_a = self._cmp_combo_a.currentData()
        mac_b = self._cmp_combo_b.currentData()
        if not mac_a or not mac_b or mac_a == mac_b:
            self._append_lab_output("Comparacion: elige dos dispositivos distintos")
            return
        from core.comparison import compare_devices, format_comparison

        result = compare_devices(self.app.database, mac_a, mac_b)
        self._append_lab_output(
            f"Comparacion {mac_a} vs {mac_b}:\n"
            + format_comparison(result, name_a="A", name_b="B")
        )

    # ==================================================================
    # Cierre ordenado
    # ==================================================================
    def closeEvent(self, event) -> None:
        self._refresh_timer.stop()
        self._log_console.detach()
        self.app.shutdown()
        event.accept()
