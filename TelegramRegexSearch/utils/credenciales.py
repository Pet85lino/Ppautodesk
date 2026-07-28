"""Carga de credenciales de la API de Telegram.

**El análisis de historiales exportados no necesita credenciales.** Este
módulo existe solo para la función opcional de descargar historiales en vivo
mediante MTProto, que requiere ``api_id`` y ``api_hash`` de my.telegram.org.

Regla que este módulo hace cumplir: las credenciales **nunca** se escriben
en el código fuente. Se leen, por orden de prioridad, de:

1. Las variables de entorno ``TELEGRAM_API_ID`` y ``TELEGRAM_API_HASH``.
2. Un archivo ``credenciales.json`` en la carpeta base del proyecto.

``credenciales.json`` está incluido en ``.gitignore``: así un ``git push``
accidental no publica el ``api_hash``, que es lo que ocurre en la mayoría de
las filtraciones de este tipo. El ``api_hash`` nunca se escribe entero en los
logs; siempre aparece enmascarado.
"""

from __future__ import annotations

import json
import logging
import os
from dataclasses import dataclass
from pathlib import Path

logger = logging.getLogger(__name__)

_VAR_ENTORNO_ID = "TELEGRAM_API_ID"
_VAR_ENTORNO_HASH = "TELEGRAM_API_HASH"
_ARCHIVO_CREDENCIALES = "credenciales.json"

#: Longitud esperada del api_hash de Telegram (32 caracteres hexadecimales).
_LONGITUD_HASH = 32


@dataclass(frozen=True, slots=True)
class CredencialesTelegram:
    """Credenciales de aplicación de Telegram.

    Attributes:
        api_id: Identificador numérico de la aplicación (no es secreto).
        api_hash: Clave secreta de la aplicación. Trátala como una contraseña.
    """

    api_id: int
    api_hash: str

    def enmascarado(self) -> str:
        """Devuelve una representación segura para registrar en los logs."""
        if len(self.api_hash) <= 8:
            return f"api_id={self.api_id}, api_hash=***"
        return f"api_id={self.api_id}, api_hash={self.api_hash[:4]}...{self.api_hash[-4:]}"


def cargar_credenciales(base_dir: Path) -> CredencialesTelegram | None:
    """Carga las credenciales del entorno o de ``credenciales.json``.

    Args:
        base_dir: Carpeta base del proyecto.

    Returns:
        Las credenciales, o ``None`` si no hay ninguna configurada o son
        inválidas. Nunca lanza excepción: la función en vivo es opcional y su
        ausencia no debe impedir el análisis de archivos ya exportados.
    """
    credenciales = _desde_entorno() or _desde_archivo(base_dir / _ARCHIVO_CREDENCIALES)

    if credenciales is None:
        logger.debug(
            "No hay credenciales de Telegram configuradas. "
            "No hacen falta para analizar exports ya descargados."
        )
        return None

    logger.info("Credenciales de Telegram cargadas (%s).", credenciales.enmascarado())
    return credenciales


def _desde_entorno() -> CredencialesTelegram | None:
    """Intenta construir las credenciales desde las variables de entorno."""
    api_id = os.environ.get(_VAR_ENTORNO_ID, "").strip()
    api_hash = os.environ.get(_VAR_ENTORNO_HASH, "").strip()

    if not api_id or not api_hash:
        return None

    return _validar(api_id, api_hash, origen="variables de entorno")


def _desde_archivo(ruta: Path) -> CredencialesTelegram | None:
    """Intenta construir las credenciales desde ``credenciales.json``."""
    if not ruta.is_file():
        return None

    try:
        with ruta.open("r", encoding="utf-8") as archivo:
            datos = json.load(archivo)
    except (json.JSONDecodeError, OSError, UnicodeDecodeError) as exc:
        logger.error("No se pudo leer %s: %s", ruta.name, exc)
        return None

    if not isinstance(datos, dict):
        logger.error("%s debe contener un objeto JSON.", ruta.name)
        return None

    return _validar(
        str(datos.get("api_id", "")).strip(),
        str(datos.get("api_hash", "")).strip(),
        origen=ruta.name,
    )


def _validar(api_id: str, api_hash: str, origen: str) -> CredencialesTelegram | None:
    """Valida el formato de las credenciales sin volcarlas nunca en el log."""
    if not api_id or not api_hash:
        logger.error("Credenciales incompletas en %s: faltan api_id o api_hash.", origen)
        return None

    try:
        identificador = int(api_id)
    except ValueError:
        logger.error("El api_id de %s no es un número entero.", origen)
        return None

    if identificador <= 0:
        logger.error("El api_id de %s debe ser un entero positivo.", origen)
        return None

    if len(api_hash) != _LONGITUD_HASH or not all(c in "0123456789abcdefABCDEF" for c in api_hash):
        logger.error(
            "El api_hash de %s no tiene el formato esperado "
            "(%d caracteres hexadecimales).",
            origen,
            _LONGITUD_HASH,
        )
        return None

    return CredencialesTelegram(api_id=identificador, api_hash=api_hash)
