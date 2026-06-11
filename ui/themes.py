"""
ui/themes.py
------------
Tema visual de la suite: oscuro con estetica glassmorphism/cyberpunk.

Paleta:
    * Fondo: degradado azul-noche profundo.
    * Paneles: superficies translucidas con borde sutil (efecto vidrio).
    * Acento: cian neon (#00E5FF) y magenta (#FF2E97) para alertas.
"""

# ----------------------------------------------------------------------
# Colores base (reutilizables desde widgets.py / dashboard.py)
# ----------------------------------------------------------------------
ACCENT = "#00E5FF"
ACCENT_DIM = "#0E7490"
ALERT = "#FF2E97"
TEXT_PRIMARY = "#E6F1FF"
TEXT_SECONDARY = "#7A8BA3"
GLASS_BG = "rgba(255, 255, 255, 0.055)"
GLASS_BORDER = "rgba(255, 255, 255, 0.12)"

# Umbrales de color del indicador de bateria.
BATTERY_GOOD = "#2EE6A8"   # > 50 %
BATTERY_WARN = "#FFC94D"   # 20-50 %
BATTERY_LOW = "#FF2E97"    # < 20 %

# Colores de estado de dispositivos (tabla del dashboard).
STATUS_CONNECTED_COLOR = "#2EE6A8"   # conectado a Windows
STATUS_PAIRED_COLOR = "#FFC94D"      # emparejado, no conectado
STATUS_NEARBY_COLOR = "#7A8BA3"      # solo visible por BLE

# ----------------------------------------------------------------------
# Hoja de estilos global (QSS)
# ----------------------------------------------------------------------
DARK_GLASS_QSS = f"""
/* ------- Base ------- */
QMainWindow {{
    background-color: qlineargradient(
        x1: 0, y1: 0, x2: 1, y2: 1,
        stop: 0 #060B16, stop: 0.5 #0A1428, stop: 1 #081020
    );
}}
QWidget {{
    color: {TEXT_PRIMARY};
    font-family: "Segoe UI", "Inter", sans-serif;
    font-size: 13px;
}}

/* ------- Paneles vidrio ------- */
QFrame#glassPanel {{
    background-color: {GLASS_BG};
    border: 1px solid {GLASS_BORDER};
    border-radius: 14px;
}}
QFrame#sidebar {{
    background-color: rgba(10, 18, 36, 0.85);
    border-right: 1px solid {GLASS_BORDER};
}}
QLabel#appTitle {{
    color: {ACCENT};
    font-size: 17px;
    font-weight: bold;
    letter-spacing: 1px;
}}
QLabel#sectionTitle {{
    color: {TEXT_PRIMARY};
    font-size: 15px;
    font-weight: 600;
}}
QLabel#mutedText {{
    color: {TEXT_SECONDARY};
    font-size: 12px;
}}

/* ------- Botones ------- */
QPushButton {{
    background-color: rgba(0, 229, 255, 0.10);
    border: 1px solid {ACCENT_DIM};
    border-radius: 9px;
    padding: 8px 16px;
    color: {ACCENT};
    font-weight: 600;
}}
QPushButton:hover {{
    background-color: rgba(0, 229, 255, 0.22);
    border-color: {ACCENT};
}}
QPushButton:pressed {{
    background-color: rgba(0, 229, 255, 0.32);
}}
QPushButton:disabled {{
    color: {TEXT_SECONDARY};
    border-color: rgba(255, 255, 255, 0.08);
    background-color: rgba(255, 255, 255, 0.03);
}}
QPushButton#navButton {{
    background-color: transparent;
    border: none;
    border-radius: 9px;
    text-align: left;
    padding: 10px 16px;
    color: {TEXT_SECONDARY};
}}
QPushButton#navButton:hover {{
    background-color: rgba(0, 229, 255, 0.08);
    color: {TEXT_PRIMARY};
}}
QPushButton#navButton:checked {{
    background-color: rgba(0, 229, 255, 0.15);
    color: {ACCENT};
}}

/* ------- Tabla de dispositivos ------- */
QTableWidget {{
    background-color: transparent;
    border: none;
    gridline-color: rgba(255, 255, 255, 0.05);
    selection-background-color: rgba(0, 229, 255, 0.18);
    selection-color: {TEXT_PRIMARY};
}}
QHeaderView::section {{
    background-color: rgba(255, 255, 255, 0.04);
    color: {TEXT_SECONDARY};
    border: none;
    padding: 7px;
    font-weight: 600;
}}
QTableWidget::item {{
    padding: 6px;
}}

/* ------- Indicador de bateria ------- */
QProgressBar {{
    background-color: rgba(255, 255, 255, 0.06);
    border: 1px solid {GLASS_BORDER};
    border-radius: 8px;
    text-align: center;
    color: {TEXT_PRIMARY};
    font-weight: bold;
    min-height: 18px;
}}
QProgressBar::chunk {{
    border-radius: 7px;
    background-color: {BATTERY_GOOD};
}}

/* ------- Desplegables (QComboBox) -------
   Sin esto, Windows pinta la lista emergente con su estilo nativo
   claro y el texto queda blanco sobre blanco (ilegible). */
QComboBox {{
    background-color: rgba(255, 255, 255, 0.06);
    border: 1px solid {GLASS_BORDER};
    border-radius: 8px;
    padding: 6px 28px 6px 10px;
    color: {TEXT_PRIMARY};
}}
QComboBox:hover {{
    border-color: {ACCENT_DIM};
}}
QComboBox:focus {{
    border-color: {ACCENT};
}}
QComboBox::drop-down {{
    border: none;
    width: 24px;
}}
QComboBox::down-arrow {{
    image: none;
    border-left: 5px solid transparent;
    border-right: 5px solid transparent;
    border-top: 6px solid {ACCENT};
    margin-right: 8px;
}}
QComboBox QAbstractItemView {{
    background-color: #0E1A30;
    border: 1px solid {ACCENT_DIM};
    border-radius: 8px;
    color: {TEXT_PRIMARY};
    padding: 4px;
    outline: none;
    selection-background-color: rgba(0, 229, 255, 0.25);
    selection-color: #FFFFFF;
}}
QComboBox QAbstractItemView::item {{
    min-height: 26px;
    padding: 4px 8px;
    color: {TEXT_PRIMARY};
}}
QComboBox QAbstractItemView::item:hover {{
    background-color: rgba(0, 229, 255, 0.15);
    color: #FFFFFF;
}}

/* ------- Tooltips y menus (mismo problema de estilo nativo) ------- */
QToolTip {{
    background-color: #0E1A30;
    color: {TEXT_PRIMARY};
    border: 1px solid {ACCENT_DIM};
    border-radius: 6px;
    padding: 6px 8px;
}}
QMenu {{
    background-color: #0E1A30;
    color: {TEXT_PRIMARY};
    border: 1px solid {GLASS_BORDER};
    border-radius: 8px;
    padding: 4px;
}}
QMenu::item:selected {{
    background-color: rgba(0, 229, 255, 0.2);
    color: #FFFFFF;
}}

/* ------- Consola de logs ------- */
QPlainTextEdit {{
    background-color: rgba(0, 0, 0, 0.35);
    border: 1px solid {GLASS_BORDER};
    border-radius: 10px;
    color: #9FE8FF;
    font-family: "Cascadia Code", "Consolas", monospace;
    font-size: 12px;
}}

/* ------- Scrollbars ------- */
QScrollBar:vertical {{
    background: transparent;
    width: 8px;
}}
QScrollBar::handle:vertical {{
    background: rgba(0, 229, 255, 0.35);
    border-radius: 4px;
    min-height: 30px;
}}
QScrollBar::add-line, QScrollBar::sub-line {{
    height: 0;
}}

/* ------- Barra de estado ------- */
QStatusBar {{
    background: rgba(10, 18, 36, 0.9);
    color: {TEXT_SECONDARY};
    border-top: 1px solid {GLASS_BORDER};
}}
"""


def battery_color(level: int) -> str:
    """Color del indicador segun el nivel de carga."""
    if level > 50:
        return BATTERY_GOOD
    if level > 20:
        return BATTERY_WARN
    return BATTERY_LOW
