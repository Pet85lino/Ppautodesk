"""
core/config.py
--------------
Carga y acceso centralizado a la configuracion de la aplicacion (config.json).

Si el archivo no existe o esta corrupto, se usan valores por defecto para que
la aplicacion siga siendo funcional (arquitectura tolerante a fallos).
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger("lino.core.config")

# Raiz del proyecto (carpeta donde vive main.py / config.json)
PROJECT_ROOT = Path(__file__).resolve().parent.parent

# Configuracion por defecto: garantiza que la app arranque aunque
# config.json falte o este danado.
DEFAULT_CONFIG: dict[str, Any] = {
    "app_name": "LINO Audio Diagnostic",
    "version": "0.1.0",
    "scan": {
        "duration_seconds": 4.0,
        "auto_refresh_ms": 5000,
    },
    "battery": {
        "poll_interval_ms": 15000,
        "connect_timeout_seconds": 10.0,
    },
    "database": {
        "path": "lino_diagnostic.db",
    },
    "logging": {
        "level": "INFO",
    },
    "ui": {
        "window_width": 1100,
        "window_height": 680,
    },
}


class Config:
    """Acceso de solo lectura a la configuracion con notacion por puntos.

    Ejemplo:
        cfg = Config.load()
        cfg.get("scan.auto_refresh_ms")  -> 5000
    """

    def __init__(self, data: dict[str, Any]):
        self._data = data

    @classmethod
    def load(cls, path: Path | None = None) -> "Config":
        """Carga config.json fusionado sobre los valores por defecto."""
        config_path = path or (PROJECT_ROOT / "config.json")
        data = json.loads(json.dumps(DEFAULT_CONFIG))  # copia profunda

        try:
            with open(config_path, "r", encoding="utf-8") as fh:
                user_data = json.load(fh)
            _deep_merge(data, user_data)
            logger.info("Configuracion cargada desde %s", config_path)
        except FileNotFoundError:
            logger.warning("config.json no encontrado, usando valores por defecto")
        except (json.JSONDecodeError, OSError) as exc:
            logger.error("Error leyendo config.json (%s), usando defaults", exc)

        return cls(data)

    def get(self, dotted_key: str, default: Any = None) -> Any:
        """Obtiene un valor con clave tipo 'scan.duration_seconds'."""
        node: Any = self._data
        for part in dotted_key.split("."):
            if not isinstance(node, dict) or part not in node:
                return default
            node = node[part]
        return node

    @property
    def database_path(self) -> Path:
        """Ruta absoluta del archivo SQLite (relativa a la raiz del proyecto)."""
        raw = Path(self.get("database.path", "lino_diagnostic.db"))
        return raw if raw.is_absolute() else PROJECT_ROOT / raw


def _deep_merge(base: dict, override: dict) -> None:
    """Fusion recursiva: los valores del usuario sobreescriben los defaults."""
    for key, value in override.items():
        if isinstance(value, dict) and isinstance(base.get(key), dict):
            _deep_merge(base[key], value)
        else:
            base[key] = value
