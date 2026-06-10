"""
core/logger.py
--------------
Configuracion del sistema de logs de toda la suite.

Salidas:
    * Consola (stdout): seguimiento en tiempo real.
    * Archivo logs/lino.log con rotacion: historial tecnico persistente.

Todos los modulos usan loggers con prefijo 'lino.*' para poder filtrar
y redirigir (por ejemplo, hacia el panel de logs de la UI).
"""

from __future__ import annotations

import logging
import sys
from logging.handlers import RotatingFileHandler
from pathlib import Path

LOG_FORMAT = "%(asctime)s | %(levelname)-7s | %(name)-20s | %(message)s"
DATE_FORMAT = "%H:%M:%S"

LOGS_DIR = Path(__file__).resolve().parent.parent / "logs"


def setup_logging(level_name: str = "INFO") -> logging.Logger:
    """Inicializa el logging global (consola + archivo rotativo).

    Args:
        level_name: nivel textual ("DEBUG", "INFO", "WARNING", "ERROR").

    Returns:
        Logger raiz de la aplicacion ('lino').
    """
    level = getattr(logging, level_name.upper(), logging.INFO)
    formatter = logging.Formatter(LOG_FORMAT, datefmt=DATE_FORMAT)

    root = logging.getLogger("lino")
    root.setLevel(level)

    # Evitar handlers duplicados si se llama dos veces (p.ej. en tests).
    if not root.handlers:
        console = logging.StreamHandler(sys.stdout)
        console.setFormatter(formatter)
        root.addHandler(console)

        try:
            LOGS_DIR.mkdir(parents=True, exist_ok=True)
            file_handler = RotatingFileHandler(
                LOGS_DIR / "lino.log",
                maxBytes=1_000_000,
                backupCount=3,
                encoding="utf-8",
            )
            file_handler.setFormatter(formatter)
            root.addHandler(file_handler)
        except OSError as exc:
            # Sin permiso de escritura: la app sigue con consola unicamente.
            root.warning("No se pudo crear logs/lino.log: %s", exc)

    # Silenciar verbosidad interna de bleak salvo en modo DEBUG.
    if level > logging.DEBUG:
        logging.getLogger("bleak").setLevel(logging.WARNING)

    return root
