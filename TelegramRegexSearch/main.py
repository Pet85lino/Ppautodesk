"""TelegramRegexSearch - Punto de entrada principal.

Busca patrones regex dentro de historiales de Telegram exportados por el
propio usuario, en formato JSON o HTML.

Uso básico::

    python main.py

Opciones::

    python main.py --modo finditer --datos "C:/mis_exports" --verbose

Todas las carpetas necesarias se crean automáticamente, y ``config.json``
se genera con valores por defecto en la primera ejecución.
"""

from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path
from typing import Iterable, Iterator, Sequence

from config.config_manager import Configuracion, cargar_configuracion
from core.descargador import FILTROS_SERVIDOR
from core.models import MODOS_VALIDOS, Mensaje, ModoBusqueda
from core.parser_html import iterar_mensajes_html
from core.parser_json import iterar_archivos, iterar_mensajes
from core.regex_loader import cargar_patrones
from core.search_engine import MotorBusqueda
from io_utils.exportador import ExportadorResultados
from io_utils.progress import RastreadorProgreso
from utils.dependencias import verificar_entorno
from utils.filesystem import crear_carpetas, resolver_ruta
from utils.logger_setup import (
    cerrar_logging,
    configurar_logging,
    iniciar_captura_temprana,
    volcar_captura_temprana,
)

logger = logging.getLogger(__name__)

#: Códigos de salida del proceso.
EXITO = 0
ERROR_FATAL = 1
SIN_TRABAJO = 2
INTERRUMPIDO = 130

_EXTENSIONES_HTML = frozenset({".html", ".htm"})


def parsear_argumentos(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Define y parsea los argumentos de línea de comandos.

    Args:
        argv: Lista de argumentos. Si es ``None`` se usan los de ``sys.argv``.

    Returns:
        Espacio de nombres con los argumentos ya parseados.
    """
    parser = argparse.ArgumentParser(
        prog="TelegramRegexSearch",
        description="Busca patrones regex en tus historiales exportados de Telegram.",
    )
    parser.add_argument(
        "--base-dir",
        type=Path,
        default=Path(__file__).resolve().parent,
        help="Carpeta base del proyecto (por defecto, la que contiene main.py).",
    )
    parser.add_argument(
        "--modo",
        choices=list(MODOS_VALIDOS),
        default=None,
        help="Modo de búsqueda. Tiene prioridad sobre config.json.",
    )
    parser.add_argument(
        "--datos",
        type=Path,
        default=None,
        help="Carpeta con los exports a analizar. Tiene prioridad sobre config.json.",
    )
    parser.add_argument(
        "--patrones",
        type=Path,
        default=None,
        help="Archivo de patrones a usar. Tiene prioridad sobre config.json.",
    )
    parser.add_argument(
        "--sin-progreso",
        action="store_true",
        help="Desactiva la barra de progreso.",
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="Muestra por consola también los mensajes de depuración.",
    )

    grupo = parser.add_argument_group(
        "descarga en vivo (opcional)",
        "Requiere Telethon y credenciales de my.telegram.org. Descarga los "
        "historiales a la carpeta de datos y después los analiza con normalidad.",
    )
    grupo.add_argument(
        "--descargar",
        action="store_true",
        help="Descarga historiales desde Telegram antes de analizarlos.",
    )
    grupo.add_argument(
        "--chat",
        action="append",
        default=None,
        metavar="CHAT",
        help="Chat a descargar (usuario, enlace o ID). Repetible. Por defecto, todos.",
    )
    grupo.add_argument(
        "--limite",
        type=int,
        default=None,
        help="Máximo de mensajes a descargar por chat.",
    )
    grupo.add_argument(
        "--consulta",
        default=None,
        help="Búsqueda por texto ejecutada en el servidor de Telegram (no admite regex).",
    )
    grupo.add_argument(
        "--filtro",
        choices=sorted(FILTROS_SERVIDOR),
        default="todos",
        help="Filtro por tipo de contenido aplicado en el servidor.",
    )
    grupo.add_argument(
        "--instalar-dependencias",
        action="store_true",
        help="Instala con pip los paquetes opcionales que falten.",
    )
    return parser.parse_args(argv)


def ejecutar(argumentos: argparse.Namespace) -> int:
    """Ejecuta el pipeline completo de TelegramRegexSearch.

    Args:
        argumentos: Argumentos ya parseados de la línea de comandos.

    Returns:
        Código de salida del proceso.
    """
    base_dir: Path = argumentos.base_dir.resolve()

    # Los avisos que genere la lectura de config.json se retienen hasta que
    # existan los archivos de log, para no perder ninguno.
    iniciar_captura_temprana()

    config = cargar_configuracion(base_dir / "config.json")
    rutas = _resolver_rutas(base_dir, config, argumentos)

    try:
        crear_carpetas(
            [
                rutas["datos"],
                rutas["resultados"],
                rutas["logs"],
                rutas["cache"],
            ]
        )
    except OSError as exc:
        print(f"ERROR: no se pudieron crear las carpetas del proyecto: {exc}", file=sys.stderr)
        return ERROR_FATAL

    configurar_logging(
        rutas["logs"],
        nivel_consola=logging.DEBUG if argumentos.verbose else logging.INFO,
    )
    volcar_captura_temprana()

    logger.info("=== Inicio de TelegramRegexSearch ===")
    logger.info("Carpeta base: %s", base_dir)

    if not verificar_entorno():
        logger.error("El entorno no cumple los requisitos mínimos. Abortando.")
        return ERROR_FATAL

    if argumentos.descargar and not _descargar(argumentos, rutas):
        return ERROR_FATAL

    modo: ModoBusqueda = argumentos.modo or config.modo_busqueda
    logger.info("Modo de búsqueda: %s", modo)

    patrones = cargar_patrones(rutas["patrones"], config.ignorar_mayusculas)
    if not patrones:
        logger.error(
            "No hay ningún patrón válido en %s. Añade al menos uno y vuelve a ejecutar.",
            rutas["patrones"],
        )
        return SIN_TRABAJO

    motor = MotorBusqueda(patrones, modo, config.evitar_duplicados)

    try:
        return _procesar(config, rutas, motor, argumentos.sin_progreso)
    except KeyboardInterrupt:
        logger.warning(
            "Ejecución interrumpida por el usuario. Los resultados parciales se conservan."
        )
        return INTERRUMPIDO
    except OSError as exc:
        logger.error("Error de entrada/salida durante el procesamiento: %s", exc)
        return ERROR_FATAL
    except Exception:  # noqa: BLE001 - último recinto: se registra la traza completa
        logger.exception("Error inesperado durante el procesamiento.")
        return ERROR_FATAL
    finally:
        logger.info("=== Fin de TelegramRegexSearch ===")


def _resolver_rutas(
    base_dir: Path, config: Configuracion, argumentos: argparse.Namespace
) -> dict[str, Path]:
    """Combina config.json y los argumentos de consola en rutas absolutas."""
    datos = (
        argumentos.datos.expanduser().resolve()
        if argumentos.datos is not None
        else resolver_ruta(base_dir, config.carpeta_datos)
    )
    patrones = (
        argumentos.patrones.expanduser().resolve()
        if argumentos.patrones is not None
        else resolver_ruta(base_dir, config.archivo_patrones)
    )
    return {
        "base": base_dir,
        "datos": datos,
        "patrones": patrones,
        "resultados": resolver_ruta(base_dir, config.carpeta_resultados),
        "logs": resolver_ruta(base_dir, config.carpeta_logs),
        "cache": resolver_ruta(base_dir, config.carpeta_cache),
    }


def _descargar(argumentos: argparse.Namespace, rutas: dict[str, Path]) -> bool:
    """Descarga historiales en vivo antes de analizarlos.

    Es un paso opcional: requiere Telethon y credenciales. Cualquier fallo se
    registra y aborta la ejecución, porque continuar analizaría datos
    antiguos haciendo creer al usuario que son los recién descargados.

    Args:
        argumentos: Argumentos de consola ya parseados.
        rutas: Rutas resueltas del proyecto.

    Returns:
        ``True`` si la descarga se completó.
    """
    import asyncio

    from core.descargador import ErrorDescarga, descargar_historiales, preparar_dependencias
    from utils.credenciales import cargar_credenciales

    if not preparar_dependencias(instalar=argumentos.instalar_dependencias):
        logger.error(
            "Falta Telethon para la descarga en vivo. Instálalo con "
            "'pip install telethon' o repite el comando añadiendo "
            "--instalar-dependencias."
        )
        return False

    credenciales = cargar_credenciales(rutas["base"])
    if credenciales is None:
        logger.error(
            "No hay credenciales configuradas. Define TELEGRAM_API_ID y "
            "TELEGRAM_API_HASH, o copia credenciales.ejemplo.json a "
            "credenciales.json y rellénalo con tus datos de my.telegram.org."
        )
        return False

    try:
        generados = asyncio.run(
            descargar_historiales(
                credenciales=credenciales,
                carpeta_destino=rutas["datos"],
                carpeta_sesion=rutas["cache"],
                chats=argumentos.chat,
                limite=argumentos.limite,
                consulta=argumentos.consulta,
                filtro=argumentos.filtro,
            )
        )
    except ErrorDescarga as exc:
        logger.error("No se pudo completar la descarga: %s", exc)
        return False
    except Exception as exc:  # noqa: BLE001 - se registra y se aborta con claridad
        logger.error("Error inesperado durante la descarga: %s", exc)
        return False

    logger.info("Descarga completada: %d archivo(s) en %s", len(generados), rutas["datos"])
    return True


def _procesar(
    config: Configuracion,
    rutas: dict[str, Path],
    motor: MotorBusqueda,
    sin_progreso: bool,
) -> int:
    """Recorre los archivos de datos, busca coincidencias y las exporta."""
    tam_bloque = config.tam_buffer_lectura_kb * 1024
    archivos = list(iterar_archivos(rutas["datos"], config.extensiones_soportadas))

    if not archivos:
        logger.error(
            "No hay archivos que analizar en %s. Copia ahí tus exports de Telegram.",
            rutas["datos"],
        )
        return SIN_TRABAJO

    logger.info("Archivos a analizar: %d", len(archivos))

    progreso = RastreadorProgreso(
        config.actualizar_progreso_cada_n_mensajes,
        forzar_activo=False if sin_progreso else None,
    )

    with progreso, ExportadorResultados(
        rutas["resultados"],
        config.codificacion_salida,
        config.max_archivos_resultado_abiertos,
    ) as exportador:
        for ruta in archivos:
            logger.debug("Procesando %s", ruta.name)
            progreso.registrar_archivo()

            mensajes = _contar_progreso(
                _leer_mensajes(ruta, tam_bloque),
                progreso,
                motor.cantidad_patrones,
            )

            for coincidencia in motor.buscar(mensajes):
                progreso.registrar_coincidencia()
                exportador.exportar(coincidencia)

        coincidencias_escritas = exportador.coincidencias_escritas
        archivos_generados = exportador.archivos_generados

    _registrar_resumen(progreso, motor, coincidencias_escritas, archivos_generados, rutas)
    return EXITO


def _leer_mensajes(ruta: Path, tam_bloque: int) -> Iterator[Mensaje]:
    """Elige el parser adecuado según la extensión del archivo."""
    if ruta.suffix.lower() in _EXTENSIONES_HTML:
        yield from iterar_mensajes_html(ruta, tam_bloque)
    else:
        yield from iterar_mensajes(ruta, tam_bloque)


def _contar_progreso(
    mensajes: Iterable[Mensaje], progreso: RastreadorProgreso, cantidad_patrones: int
) -> Iterator[Mensaje]:
    """Envuelve el flujo de mensajes para ir actualizando los contadores."""
    for mensaje in mensajes:
        progreso.registrar_mensaje()
        progreso.registrar_evaluaciones_regex(cantidad_patrones)
        yield mensaje


def _registrar_resumen(
    progreso: RastreadorProgreso,
    motor: MotorBusqueda,
    coincidencias_escritas: int,
    archivos_generados: int,
    rutas: dict[str, Path],
) -> None:
    """Vuelca en el log el resumen final de la ejecución."""
    logger.info(
        "Resumen: %d archivos | %d mensajes | %d evaluaciones de regex | "
        "%d coincidencias | %.1f mensajes/s | %.1fs en total",
        progreso.archivos_procesados,
        progreso.mensajes_procesados,
        progreso.regex_evaluados,
        coincidencias_escritas,
        progreso.mensajes_por_segundo(),
        progreso.tiempo_transcurrido(),
    )

    if motor.duplicados_descartados:
        logger.info("Coincidencias duplicadas descartadas: %d", motor.duplicados_descartados)

    mas_lento = motor.patron_mas_lento()
    if mas_lento is not None:
        logger.debug("Patrón que más tiempo consumió: '%s' (%.2fs)", mas_lento[0], mas_lento[1])

    if coincidencias_escritas:
        logger.info(
            "Resultados escritos en %d archivo(s): %s", archivos_generados, rutas["resultados"]
        )
    else:
        logger.warning(
            "No se encontró ninguna coincidencia. Revisa tus patrones en %s", rutas["patrones"]
        )


def main(argv: Sequence[str] | None = None) -> int:
    """Punto de entrada del script.

    Args:
        argv: Argumentos de consola, o ``None`` para tomarlos de ``sys.argv``.

    Returns:
        Código de salida del proceso.
    """
    argumentos = parsear_argumentos(argv)
    try:
        return ejecutar(argumentos)
    finally:
        cerrar_logging()


if __name__ == "__main__":
    sys.exit(main())
