"""Utilidades de sistema de archivos.

Contiene funciones reutilizables para crear la estructura de carpetas del
proyecto y para sanear nombres de archivo antes de escribirlos en disco,
evitando caracteres inválidos y nombres reservados de Windows.
"""

from __future__ import annotations

import logging
import re
from pathlib import Path
from typing import Iterable

logger = logging.getLogger(__name__)

# Caracteres no permitidos en nombres de archivo en Windows (y de paso, los
# de control, que tampoco son válidos en Android/Linux).
_CARACTERES_INVALIDOS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')

# Nombres reservados por Windows: no se pueden usar ni con extensión.
_NOMBRES_RESERVADOS_WINDOWS = frozenset(
    {"CON", "PRN", "AUX", "NUL"}
    | {f"COM{i}" for i in range(1, 10)}
    | {f"LPT{i}" for i in range(1, 10)}
)

_NOMBRE_POR_DEFECTO = "patron_sin_nombre"


def crear_carpetas(carpetas: Iterable[Path]) -> None:
    """Crea las carpetas indicadas si no existen.

    Args:
        carpetas: Rutas de carpetas a crear (se crean los padres si hace falta).

    Raises:
        OSError: Si alguna carpeta no se puede crear (permisos, disco lleno...).
    """
    for carpeta in carpetas:
        carpeta.mkdir(parents=True, exist_ok=True)
        logger.debug("Carpeta disponible: %s", carpeta)


def sanear_nombre_archivo(nombre: str, max_length: int = 120) -> str:
    """Convierte una cadena en un nombre de archivo válido y seguro.

    Reemplaza caracteres inválidos por guion bajo, elimina separadores de
    ruta (evitando escrituras fuera de la carpeta de destino), esquiva los
    nombres reservados de Windows y limita la longitud para no exceder los
    límites del sistema de archivos.

    Args:
        nombre: Nombre original propuesto (normalmente el nombre del patrón).
        max_length: Longitud máxima permitida del nombre resultante.

    Returns:
        Un nombre de archivo saneado, nunca vacío y sin componentes de ruta.
    """
    nombre_limpio = _CARACTERES_INVALIDOS.sub("_", nombre)
    # Un nombre como ".." se neutraliza al quitar puntos y espacios de los
    # extremos, impidiendo cualquier salto de directorio.
    nombre_limpio = nombre_limpio.strip().strip(". ").strip()

    if not nombre_limpio:
        return _NOMBRE_POR_DEFECTO

    if nombre_limpio.split(".")[0].upper() in _NOMBRES_RESERVADOS_WINDOWS:
        nombre_limpio = f"_{nombre_limpio}"

    return nombre_limpio[:max_length]


def resolver_ruta(base_dir: Path, ruta_configurada: str) -> Path:
    """Resuelve una ruta de config.json contra la carpeta base del proyecto.

    Las rutas relativas se interpretan respecto a ``base_dir``, de modo que
    el proyecto funciona igual sin importar desde qué directorio se invoque.
    Las rutas absolutas se respetan tal cual.

    Args:
        base_dir: Carpeta base del proyecto.
        ruta_configurada: Ruta tal como aparece en config.json.

    Returns:
        Ruta absoluta lista para usarse.
    """
    ruta = Path(ruta_configurada).expanduser()
    return ruta if ruta.is_absolute() else (base_dir / ruta)
