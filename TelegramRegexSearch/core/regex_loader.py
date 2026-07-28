"""Carga y compilación de patrones regex desde patrones.txt.

Formato del archivo:
    - Un patrón regex por línea.
    - Las líneas que empiezan con ``#`` son comentarios y se ignoran.
    - Las líneas vacías o solo con espacios se ignoran.
    - Opcionalmente se puede nombrar un patrón con la sintaxis::

          nombre_patron :: regex

      Si no se indica nombre, se usa ``patron_<numero_de_linea>``.

Un patrón que no compile se registra en ``logs/error.log`` y se descarta,
pero no interrumpe la carga de los demás: es preferible buscar con nueve
patrones válidos y un aviso claro que abortar toda la ejecución.
"""

from __future__ import annotations

import logging
import re
from pathlib import Path

from core.models import PatronRegex

logger = logging.getLogger(__name__)

_SEPARADOR_NOMBRE = "::"
_COMENTARIO = "#"


def cargar_patrones(ruta_patrones: Path, ignorar_mayusculas: bool = False) -> list[PatronRegex]:
    """Lee patrones.txt y devuelve la lista de patrones válidos y compilados.

    Args:
        ruta_patrones: Ruta al archivo de patrones.
        ignorar_mayusculas: Si es ``True``, todos los patrones se compilan con
            :data:`re.IGNORECASE`. Para hacerlo patrón a patrón puede usarse
            el prefijo estándar ``(?i)`` dentro del propio regex.

    Returns:
        Lista de instancias de :class:`core.models.PatronRegex` compiladas.
        Puede estar vacía si el archivo no existe o no contiene patrones
        válidos; corresponde al llamante decidir qué hacer en ese caso.
    """
    if not ruta_patrones.is_file():
        logger.error("No se encontró el archivo de patrones: %s", ruta_patrones)
        return []

    banderas = re.IGNORECASE if ignorar_mayusculas else 0
    patrones: list[PatronRegex] = []
    nombres_usados: set[str] = set()
    invalidos = 0

    try:
        with ruta_patrones.open("r", encoding="utf-8", errors="replace") as archivo:
            for numero_linea, linea_cruda in enumerate(archivo, start=1):
                linea = linea_cruda.strip()

                if not linea or linea.startswith(_COMENTARIO):
                    continue

                nombre, regex_str = _separar_nombre_y_regex(linea, numero_linea)

                if not regex_str:
                    logger.error("Línea %d: patrón vacío tras '::', se omite.", numero_linea)
                    invalidos += 1
                    continue

                try:
                    compilado = re.compile(regex_str, banderas)
                except re.error as exc:
                    logger.error(
                        "Regex inválido en la línea %d (%s): %s", numero_linea, regex_str, exc
                    )
                    invalidos += 1
                    continue

                nombre = _nombre_unico(nombre, numero_linea, nombres_usados)
                patrones.append(
                    PatronRegex(
                        nombre=nombre,
                        regex=regex_str,
                        compilado=compilado,
                        numero_linea=numero_linea,
                    )
                )
    except OSError as exc:
        logger.error("No se pudo leer el archivo de patrones %s: %s", ruta_patrones, exc)
        return []

    logger.info("Patrones cargados: %d válidos, %d inválidos.", len(patrones), invalidos)
    return patrones


def _separar_nombre_y_regex(linea: str, numero_linea: int) -> tuple[str, str]:
    """Separa una línea de patrones.txt en la tupla ``(nombre, regex)``.

    Solo se interpreta como nombre la parte anterior al **primer** ``::``, y
    únicamente si no contiene caracteres típicos de una expresión regular.
    Así, un patrón legítimo como ``a::b`` no se parte por error.
    """
    if _SEPARADOR_NOMBRE not in linea:
        return f"patron_{numero_linea}", linea

    posible_nombre, _, resto = linea.partition(_SEPARADOR_NOMBRE)
    nombre = posible_nombre.strip()

    if not nombre or _parece_regex(nombre):
        return f"patron_{numero_linea}", linea

    return nombre, resto.strip()


def _parece_regex(texto: str) -> bool:
    """Indica si un texto contiene metacaracteres propios de una expresión regular."""
    return any(caracter in texto for caracter in r"\[](){}*+?|^$")


def _nombre_unico(nombre: str, numero_linea: int, nombres_usados: set[str]) -> str:
    """Garantiza que cada patrón tenga un nombre distinto.

    Dos patrones con el mismo nombre escribirían en el mismo archivo de
    resultados y sus coincidencias quedarían mezcladas sin aviso.
    """
    if nombre not in nombres_usados:
        nombres_usados.add(nombre)
        return nombre

    nombre_alternativo = f"{nombre}_{numero_linea}"
    logger.warning(
        "Nombre de patrón duplicado '%s' en la línea %d; se renombra a '%s'.",
        nombre,
        numero_linea,
        nombre_alternativo,
    )
    nombres_usados.add(nombre_alternativo)
    return nombre_alternativo
