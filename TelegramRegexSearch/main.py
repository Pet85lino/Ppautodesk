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
from datetime import datetime
from pathlib import Path
from typing import Iterable, Iterator, Sequence

from config.config_manager import Configuracion, cargar_configuracion
from core.descargador import FILTROS_SERVIDOR, TIPOS_CHAT
from core.filtro_fechas import (
    AsignadorVentanas,
    VentanaTemporal,
    construir_ventanas,
    filtrar_mensajes,
    ventana_envolvente,
)
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
        "--dias",
        default=None,
        metavar="N[,N...]",
        help=(
            "Analiza solo los mensajes de los últimos N días. Admite varias "
            "ventanas separadas por comas (por ejemplo 30,60,90,160,180): cada "
            "una genera su propia subcarpeta en resultados/."
        ),
    )
    parser.add_argument(
        "--desde",
        default=None,
        metavar="AAAA-MM-DD",
        help="Analiza solo los mensajes a partir de esta fecha (incluida).",
    )
    parser.add_argument(
        "--hasta",
        default=None,
        metavar="AAAA-MM-DD",
        help="Analiza solo los mensajes hasta esta fecha (incluida).",
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
        "--configurar",
        action="store_true",
        help=(
            "Configura el acceso a Telegram paso a paso (credenciales y código "
            "de verificación) y comprueba que funciona. Después, termina."
        ),
    )
    grupo.add_argument(
        "--descargar",
        action="store_true",
        help="Descarga historiales desde Telegram antes de analizarlos.",
    )
    grupo.add_argument(
        "--listar-chats",
        action="store_true",
        help="Muestra los grupos y canales accesibles con su nombre e ID, y termina.",
    )
    grupo.add_argument(
        "--chat",
        action="append",
        default=None,
        metavar="CHAT",
        help=(
            "Chat concreto a descargar: ID, @usuario o parte del nombre. "
            "Repetible. Por defecto, todos los del tipo indicado."
        ),
    )
    grupo.add_argument(
        "--tipo",
        choices=list(TIPOS_CHAT),
        default="todos",
        help="Qué diálogos incluir: todos, grupos, canales o privados.",
    )
    grupo.add_argument(
        "--espera-maxima",
        type=int,
        default=300,
        help="Segundos máximos de espera cuando Telegram aplica un límite de peticiones.",
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

    if argumentos.configurar:
        return EXITO if _configurar(argumentos, rutas) else ERROR_FATAL

    if argumentos.listar_chats:
        return EXITO if _listar_chats(argumentos, rutas) else ERROR_FATAL

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

    try:
        ventanas = _construir_ventanas(argumentos, config)
    except ValueError as exc:
        logger.error("Rango de fechas inválido: %s", exc)
        return SIN_TRABAJO

    motor = MotorBusqueda(patrones, modo, config.evitar_duplicados)

    try:
        return _procesar(config, rutas, motor, argumentos.sin_progreso, ventanas)
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


def _construir_ventanas(
    argumentos: argparse.Namespace, config: Configuracion
) -> list[VentanaTemporal]:
    """Determina el rango de fechas a analizar.

    Las opciones de consola tienen prioridad sobre ``config.json``, de modo
    que la configuración fija el comportamiento habitual y la consola permite
    desviarse de él puntualmente sin editar ningún archivo.

    Args:
        argumentos: Argumentos de consola ya parseados.
        config: Configuración cargada.

    Returns:
        Lista de ventanas temporales a aplicar.

    Raises:
        ValueError: Si alguna fecha o número de días no es interpretable.
    """
    if argumentos.dias is not None:
        dias = _parsear_dias(argumentos.dias)
    else:
        dias = list(config.dias_recientes)

    desde = _parsear_fecha_argumento(argumentos.desde or config.fecha_desde, "--desde")
    hasta = _parsear_fecha_argumento(argumentos.hasta or config.fecha_hasta, "--hasta")

    if desde is not None and hasta is not None and desde > hasta:
        raise ValueError("la fecha inicial es posterior a la final")

    if hasta is not None:
        # Se incluye el día completo indicado en --hasta, no solo su medianoche.
        hasta = hasta.replace(hour=23, minute=59, second=59)

    return construir_ventanas(dias=dias, desde=desde, hasta=hasta)


def _parsear_dias(texto: str) -> list[int]:
    """Interpreta el valor de ``--dias``, que admite una lista separada por comas."""
    dias: list[int] = []
    for parte in texto.split(","):
        parte = parte.strip()
        if not parte:
            continue
        try:
            valor = int(parte)
        except ValueError:
            raise ValueError(f"'{parte}' no es un número de días válido") from None
        if valor <= 0:
            raise ValueError(f"el número de días debe ser positivo, y se recibió {valor}")
        dias.append(valor)
    return dias


def _parsear_fecha_argumento(texto: str | None, opcion: str) -> datetime | None:
    """Interpreta una fecha ``AAAA-MM-DD`` procedente de la consola o de config."""
    if not texto:
        return None
    try:
        return datetime.strptime(texto.strip(), "%Y-%m-%d")
    except ValueError:
        raise ValueError(f"{opcion} espera el formato AAAA-MM-DD y recibió '{texto}'") from None


def _configurar(argumentos: argparse.Namespace, rutas: dict[str, Path]) -> bool:
    """Configura el acceso a Telegram de principio a fin y comprueba que sirve.

    Instala Telethon si hace falta, pide las credenciales, inicia sesión con
    el código de verificación y confirma la conexión contando los chats
    accesibles. Deja la sesión guardada para que las siguientes ejecuciones no
    vuelvan a preguntar nada.

    Returns:
        ``True`` si al terminar la aplicación puede acceder a Telegram.
    """
    import asyncio

    from core.descargador import preparar_dependencias
    from core.sesion import ErrorAutenticacion, conectar
    from utils.credenciales import obtener_credenciales

    print(
        "\n"
        "==================================================\n"
        " Asistente de configuración\n"
        "==================================================\n"
    )

    if not preparar_dependencias(instalar=True):
        print(
            "\n"
            "No se pudo instalar Telethon automáticamente. El detalle está en\n"
            "logs/error.log. Instálalo a mano; suele ser cuestión de un minuto:\n"
            "\n"
            "  Pydroid 3 -> menú lateral -> Pip -> Install -> escribe 'telethon'\n"
            "               -> Install. Usa paquetes ya compilados, así que\n"
            "               funciona aunque pip por consola falle.\n"
            "\n"
            "  Windows   -> py -m pip install telethon\n"
            "\n"
            "Después vuelve a ejecutar: python main.py --configurar\n"
        )
        return False

    print("Telethon disponible.")

    credenciales = obtener_credenciales(rutas["base"])
    if credenciales is None:
        return False

    async def _probar() -> int:
        cliente = await conectar(credenciales, rutas["cache"])
        try:
            return sum([1 async for _ in cliente.iter_dialogs()])
        finally:
            await cliente.disconnect()

    try:
        total = asyncio.run(_probar())
    except ErrorAutenticacion as exc:
        print(f"\nNo se pudo completar el acceso: {exc}")
        return False
    except KeyboardInterrupt:
        print("\nConfiguración cancelada.")
        return False
    except Exception as exc:  # noqa: BLE001 - se informa con claridad y se sale
        logger.error("Error inesperado durante la configuración: %s", exc)
        return False

    print(
        f"\nTodo listo: {total} chat(s) accesibles.\n"
        "La sesión queda guardada, así que no volverá a pedirte el código.\n"
        "\n"
        "Siguientes pasos:\n"
        "  python main.py --listar-chats --tipo grupos\n"
        "  python main.py --descargar --tipo grupos --dias 30\n"
    )
    return True


def _listar_chats(argumentos: argparse.Namespace, rutas: dict[str, Path]) -> bool:
    """Muestra los chats accesibles para que el usuario elija cuáles analizar.

    Los grupos privados no tienen nombre de usuario, así que este listado es
    la única forma práctica de averiguar cómo referirse a ellos con --chat.

    Returns:
        ``True`` si el listado se pudo obtener.
    """
    import asyncio

    from core.descargador import ErrorDescarga, listar_chats, preparar_dependencias
    from utils.credenciales import obtener_credenciales

    if not preparar_dependencias(instalar=argumentos.instalar_dependencias):
        logger.error("Falta Telethon. Instálalo con 'pip install telethon'.")
        return False

    credenciales = obtener_credenciales(rutas["base"])
    if credenciales is None:
        logger.error("Sin credenciales no se puede consultar la lista de chats.")
        return False

    try:
        chats = asyncio.run(listar_chats(credenciales, rutas["cache"], argumentos.tipo))
    except ErrorDescarga as exc:
        logger.error("No se pudo obtener la lista de chats: %s", exc)
        return False
    except Exception as exc:  # noqa: BLE001 - se informa con claridad y se sale
        logger.error("Error inesperado al listar los chats: %s", exc)
        return False

    if not chats:
        logger.warning("No se encontró ningún chat del tipo '%s'.", argumentos.tipo)
        return True

    print(f"\n{'TIPO':<9} {'ID':>14}  {'USUARIO':<20} NOMBRE")
    print("-" * 78)
    for chat in chats:
        print(f"{chat['tipo']:<9} {str(chat['id']):>14}  {chat['usuario']:<20} {chat['nombre']}")
    print(f"\n{len(chats)} chat(s). Usa --chat con el ID, el @usuario o parte del nombre.\n")

    return True


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
    from utils.credenciales import obtener_credenciales

    if not preparar_dependencias(instalar=argumentos.instalar_dependencias):
        logger.error(
            "Falta Telethon para la descarga en vivo. Instálalo con "
            "'pip install telethon' o repite el comando añadiendo "
            "--instalar-dependencias."
        )
        return False

    credenciales = obtener_credenciales(rutas["base"])
    if credenciales is None:
        logger.error("Sin credenciales no se puede descargar nada de Telegram.")
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
                tipo=argumentos.tipo,
                espera_maxima=argumentos.espera_maxima,
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
    ventanas: list[VentanaTemporal],
) -> int:
    """Recorre los archivos de datos, busca coincidencias y las exporta.

    Cuando se piden varias ventanas temporales se escriben todas en el mismo
    recorrido: al ser rangos concéntricos, una coincidencia de los últimos 30
    días pertenece también a la de 60, 90 y siguientes. Recorrer los archivos
    una sola vez evita releer y reanalizar el historial completo por cada
    ventana solicitada.
    """
    tam_bloque = config.tam_buffer_lectura_kb * 1024
    archivos = list(iterar_archivos(rutas["datos"], config.extensiones_soportadas))

    if not archivos:
        logger.error(
            "No hay archivos que analizar en %s. Copia ahí tus exports de Telegram.",
            rutas["datos"],
        )
        return SIN_TRABAJO

    logger.info("Archivos a analizar: %d", len(archivos))
    _registrar_ventanas(ventanas)

    progreso = RastreadorProgreso(
        config.actualizar_progreso_cada_n_mensajes,
        forzar_activo=False if sin_progreso else None,
    )
    asignador = AsignadorVentanas(ventanas)
    envolvente = ventana_envolvente(ventanas)
    exportadores = _crear_exportadores(config, rutas, ventanas)
    escritas = 0
    generados = 0

    try:
        with progreso:
            for ruta in archivos:
                logger.debug("Procesando %s", ruta.name)
                progreso.registrar_archivo()

                mensajes = _contar_progreso(
                    filtrar_mensajes(_leer_mensajes(ruta, tam_bloque), envolvente),
                    progreso,
                    motor.cantidad_patrones,
                )

                for coincidencia in motor.buscar(mensajes):
                    progreso.registrar_coincidencia()
                    for etiqueta in asignador.etiquetas_de(coincidencia.mensaje):
                        exportadores[etiqueta].exportar(coincidencia)

            escritas = sum(exp.coincidencias_escritas for exp in exportadores.values())
            generados = sum(exp.archivos_generados for exp in exportadores.values())
    finally:
        for exportador in exportadores.values():
            exportador.cerrar()

    if asignador.fechas_ilegibles:
        logger.warning(
            "No se pudo interpretar la fecha de %d mensaje(s); se han incluido "
            "en todas las ventanas para no perder coincidencias.",
            asignador.fechas_ilegibles,
        )

    _registrar_resumen(progreso, motor, escritas, generados, rutas)
    return EXITO


def _crear_exportadores(
    config: Configuracion, rutas: dict[str, Path], ventanas: list[VentanaTemporal]
) -> dict[str, ExportadorResultados]:
    """Crea un exportador por ventana temporal, cada uno en su subcarpeta."""
    exportadores: dict[str, ExportadorResultados] = {}
    for ventana in ventanas:
        destino = rutas["resultados"]
        carpeta = destino / ventana.etiqueta if ventana.etiqueta else destino
        exportadores[ventana.etiqueta] = ExportadorResultados(
            carpeta,
            config.codificacion_salida,
            config.max_archivos_resultado_abiertos,
        )
    return exportadores


def _registrar_ventanas(ventanas: list[VentanaTemporal]) -> None:
    """Deja constancia en el log del rango de fechas que se va a analizar."""
    if len(ventanas) == 1 and ventanas[0].sin_limites:
        logger.info("Rango de fechas: todo el historial.")
        return

    for ventana in ventanas:
        desde = ventana.desde.strftime("%Y-%m-%d") if ventana.desde else "el principio"
        hasta = ventana.hasta.strftime("%Y-%m-%d") if ventana.hasta else "hoy"
        destino = f" -> resultados/{ventana.etiqueta}/" if ventana.etiqueta else ""
        logger.info("Rango de fechas: desde %s hasta %s%s", desde, hasta, destino)


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
