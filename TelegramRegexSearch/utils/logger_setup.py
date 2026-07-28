"""Configuración centralizada de logging para el proyecto.

Crea dos archivos de log:
    - ``logs/proceso.log``: flujo normal de ejecución (INFO y superior).
    - ``logs/error.log``: únicamente errores y excepciones (ERROR y superior).

Además añade salida por consola. El handler de consola está preparado para
convivir con la barra de progreso (borra la línea de progreso antes de
escribir) y para no romper en consolas de Windows con codificaciones
antiguas que no admiten emojis ni caracteres fuera de su página de códigos.
"""

from __future__ import annotations

import logging
import sys
from pathlib import Path
from typing import Callable

_FORMATO_ARCHIVO = "%(asctime)s | %(levelname)-8s | %(name)s | %(message)s"
_FORMATO_CONSOLA = "%(levelname)-8s | %(message)s"

#: Callback opcional invocado antes de escribir en consola. Lo usa la barra
#: de progreso para limpiar su línea y que los mensajes no se solapen.
_limpiar_linea_progreso: Callable[[], None] | None = None


def registrar_limpiador_de_progreso(callback: Callable[[], None] | None) -> None:
    """Registra la función que limpia la línea de progreso antes de loguear.

    Args:
        callback: Función sin argumentos que borra la línea actual de la
            consola, o ``None`` para desactivar la coordinación.
    """
    global _limpiar_linea_progreso
    _limpiar_linea_progreso = callback


class _HandlerConsolaSeguro(logging.StreamHandler):
    """Handler de consola tolerante a fallos de codificación y a la barra de progreso."""

    def emit(self, record: logging.LogRecord) -> None:
        """Escribe el registro limpiando antes la línea de progreso."""
        if _limpiar_linea_progreso is not None:
            try:
                _limpiar_linea_progreso()
            except Exception:  # noqa: BLE001 - nunca romper por la barra de progreso
                pass
        super().emit(record)

    def format(self, record: logging.LogRecord) -> str:
        """Formatea el registro sustituyendo caracteres no representables.

        En la consola de Windows (cp1252) un nombre de chat con emojis
        provocaría ``UnicodeEncodeError`` y perdería el mensaje de log.
        """
        texto = super().format(record)
        codificacion = getattr(self.stream, "encoding", None) or "utf-8"
        try:
            texto.encode(codificacion)
        except (UnicodeEncodeError, LookupError):
            texto = texto.encode(codificacion, errors="replace").decode(
                codificacion, errors="replace"
            )
        return texto


class _HandlerBufferInicial(logging.Handler):
    """Guarda en memoria los registros emitidos antes de configurar el logging."""

    def __init__(self) -> None:
        super().__init__(logging.DEBUG)
        self.registros: list[logging.LogRecord] = []

    def emit(self, record: logging.LogRecord) -> None:
        """Almacena el registro para reenviarlo más tarde."""
        self.registros.append(record)


_buffer_inicial: _HandlerBufferInicial | None = None


def iniciar_captura_temprana() -> None:
    """Empieza a retener los registros previos a la configuración definitiva.

    La carpeta de logs se decide leyendo config.json, así que los avisos que
    genere esa lectura se producen antes de que existan los archivos de log.
    Reteniéndolos aquí no se pierde ninguno.
    """
    global _buffer_inicial
    _buffer_inicial = _HandlerBufferInicial()
    logger_raiz = logging.getLogger()
    logger_raiz.setLevel(logging.DEBUG)
    logger_raiz.addHandler(_buffer_inicial)


def volcar_captura_temprana() -> None:
    """Reenvía a los handlers definitivos los registros retenidos."""
    global _buffer_inicial
    if _buffer_inicial is None:
        return

    registros = _buffer_inicial.registros
    _buffer_inicial = None

    logger_raiz = logging.getLogger()
    for registro in registros:
        for handler in logger_raiz.handlers:
            if registro.levelno >= handler.level:
                handler.handle(registro)


def configurar_logging(
    logs_dir: Path,
    nivel_consola: int = logging.INFO,
    nombre_proceso: str = "proceso.log",
    nombre_error: str = "error.log",
) -> logging.Logger:
    """Configura el logger raíz con handlers de archivo y de consola.

    Es idempotente: llamarla varias veces no duplica handlers.

    Args:
        logs_dir: Carpeta donde se guardarán los archivos de log.
        nivel_consola: Nivel mínimo mostrado por consola.
        nombre_proceso: Nombre del archivo de log de proceso.
        nombre_error: Nombre del archivo de log de errores.

    Returns:
        El logger raíz ya configurado.
    """
    logs_dir.mkdir(parents=True, exist_ok=True)

    logger_raiz = logging.getLogger()
    logger_raiz.setLevel(logging.DEBUG)

    # Cierra y elimina handlers previos para evitar duplicados y descriptores
    # de archivo huérfanos si la función se invoca más de una vez (tests).
    for handler in list(logger_raiz.handlers):
        logger_raiz.removeHandler(handler)
        handler.close()

    formato_archivo = logging.Formatter(_FORMATO_ARCHIVO)

    handler_proceso = logging.FileHandler(logs_dir / nombre_proceso, encoding="utf-8")
    handler_proceso.setLevel(logging.INFO)
    handler_proceso.setFormatter(formato_archivo)
    logger_raiz.addHandler(handler_proceso)

    handler_error = logging.FileHandler(logs_dir / nombre_error, encoding="utf-8")
    handler_error.setLevel(logging.ERROR)
    handler_error.setFormatter(formato_archivo)
    logger_raiz.addHandler(handler_error)

    handler_consola = _HandlerConsolaSeguro(stream=sys.stderr)
    handler_consola.setLevel(nivel_consola)
    handler_consola.setFormatter(logging.Formatter(_FORMATO_CONSOLA))
    logger_raiz.addHandler(handler_consola)

    return logger_raiz


def cerrar_logging() -> None:
    """Cierra los handlers de archivo y desregistra el limpiador de progreso.

    Útil al terminar el programa y, sobre todo, en los tests, para que
    Windows no mantenga bloqueados los archivos de log.
    """
    registrar_limpiador_de_progreso(None)
    logger_raiz = logging.getLogger()
    for handler in list(logger_raiz.handlers):
        logger_raiz.removeHandler(handler)
        handler.close()
