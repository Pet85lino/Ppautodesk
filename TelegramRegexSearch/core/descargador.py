"""Descarga opcional de historiales en vivo mediante MTProto.

**Este módulo es opcional y no forma parte del núcleo.** El resto del
proyecto funciona solo con la librería estándar; aquí se requiere Telethon,
que se instala aparte. Si no está disponible, el programa sigue analizando
con normalidad los exports ya descargados.

Por qué el regex sigue ejecutándose en local
--------------------------------------------
La API de Telegram (``messages.search``) **no admite expresiones
regulares**: solo busca por subcadena o palabra clave, opcionalmente
acotada por tipo de contenido (``inputMessagesFilterUrl``,
``inputMessagesFilterPhotos``, etc.). Por tanto, este módulo no sustituye
al motor de búsqueda: se limita a **traer** los mensajes y guardarlos en la
carpeta de datos con el mismo formato que un export oficial. A partir de
ahí, el pipeline habitual aplica los regex sin cambio alguno.

Sí conviene aprovechar los filtros del servidor cuando se puede: pedir solo
los mensajes con enlaces reduce enormemente lo que hay que descargar antes
de aplicar un regex de URLs en local.

Requisitos
----------
* ``pip install telethon``
* ``api_id`` y ``api_hash`` de https://my.telegram.org, cargados por
  :mod:`utils.credenciales` (variables de entorno o ``credenciales.json``).

La primera ejecución pide el teléfono y el código de confirmación, y crea
un archivo ``.session``. **Ese archivo da acceso a tu cuenta**: está en
``.gitignore`` y no debe compartirse.
"""

from __future__ import annotations

import json
import logging
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterator

from utils.credenciales import CredencialesTelegram
from utils.dependencias import Requisito, asegurar_paquetes, modulo_disponible
from utils.filesystem import sanear_nombre_archivo

logger = logging.getLogger(__name__)

#: Paquete externo necesario únicamente para esta funcionalidad.
REQUISITO_TELETHON = Requisito(
    modulo="telethon",
    paquete="telethon",
    motivo="descargar historiales en vivo desde Telegram (opcional)",
)

#: Tipos de diálogo que se pueden seleccionar para la descarga.
TIPOS_CHAT: tuple[str, ...] = ("todos", "grupos", "canales", "privados")

#: Filtros de servidor admitidos, con el nombre del tipo de Telethon.
#: Aplicarlos reduce el volumen descargado antes de ejecutar los regex.
FILTROS_SERVIDOR: dict[str, str] = {
    "todos": "",
    "enlaces": "InputMessagesFilterUrl",
    "fotos": "InputMessagesFilterPhotos",
    "videos": "InputMessagesFilterVideo",
    "documentos": "InputMessagesFilterDocument",
    "musica": "InputMessagesFilterMusic",
    "voz": "InputMessagesFilterVoice",
    "gifs": "InputMessagesFilterGif",
    "menciones": "InputMessagesFilterMyMentions",
    "fijados": "InputMessagesFilterPinned",
    "contactos": "InputMessagesFilterContacts",
    "ubicaciones": "InputMessagesFilterGeo",
}


class ErrorDescarga(Exception):
    """Error irrecuperable durante la descarga de un historial."""


def telethon_disponible() -> bool:
    """Indica si Telethon puede importarse en este entorno."""
    return modulo_disponible("telethon")


def preparar_dependencias(instalar: bool = False) -> bool:
    """Comprueba (y opcionalmente instala) las dependencias de la descarga.

    Args:
        instalar: Si es ``True``, instala Telethon con pip cuando falte.

    Returns:
        ``True`` si Telethon está disponible al terminar.
    """
    return not asegurar_paquetes([REQUISITO_TELETHON], instalar_automaticamente=instalar)


def _obtener_filtro(nombre: str) -> Any:
    """Traduce un nombre de filtro a la clase correspondiente de Telethon.

    Args:
        nombre: Clave de :data:`FILTROS_SERVIDOR`.

    Returns:
        Una instancia del filtro, o ``None`` para no filtrar.

    Raises:
        ErrorDescarga: Si el nombre no corresponde a ningún filtro conocido.
    """
    if nombre not in FILTROS_SERVIDOR:
        raise ErrorDescarga(
            f"Filtro desconocido: {nombre!r}. Disponibles: {', '.join(FILTROS_SERVIDOR)}"
        )

    nombre_tipo = FILTROS_SERVIDOR[nombre]
    if not nombre_tipo:
        return None

    from telethon.tl import types  # Importación diferida: Telethon es opcional.

    return getattr(types, nombre_tipo)()


def _texto_del_mensaje(mensaje: Any) -> str:
    """Obtiene el texto de un mensaje de Telethon, incluyendo pies de foto."""
    for atributo in ("message", "text"):
        valor = getattr(mensaje, atributo, None)
        if isinstance(valor, str) and valor:
            return valor
    return ""


def _nombre_remitente(mensaje: Any) -> tuple[str | None, str]:
    """Deduce el nombre y el identificador del remitente.

    Returns:
        Tupla ``(nombre, identificador)``; el nombre puede ser ``None`` en
        publicaciones anónimas de canal.
    """
    remitente = getattr(mensaje, "sender", None)
    identificador = str(getattr(mensaje, "sender_id", "") or "")

    if remitente is None:
        return None, identificador

    titulo = getattr(remitente, "title", None)
    if isinstance(titulo, str) and titulo:
        return titulo, identificador

    partes = [
        getattr(remitente, "first_name", None) or "",
        getattr(remitente, "last_name", None) or "",
    ]
    nombre = " ".join(parte for parte in partes if parte).strip()
    if nombre:
        return nombre, identificador

    usuario = getattr(remitente, "username", None)
    return (usuario if isinstance(usuario, str) and usuario else None), identificador


def mensaje_a_formato_export(mensaje: Any) -> dict[str, Any] | None:
    """Convierte un mensaje de Telethon al formato del export oficial.

    Producir exactamente la misma estructura que genera Telegram Desktop
    permite que :mod:`core.parser_json` lea estos archivos sin ningún caso
    especial.

    Args:
        mensaje: Objeto ``Message`` de Telethon.

    Returns:
        El diccionario del mensaje, o ``None`` si no contiene texto.
    """
    texto = _texto_del_mensaje(mensaje)
    if not texto:
        return None

    nombre, identificador = _nombre_remitente(mensaje)
    fecha = getattr(mensaje, "date", None)

    return {
        "id": getattr(mensaje, "id", None),
        "type": "message",
        "date": _formatear_fecha(fecha),
        "from": nombre,
        "from_id": identificador,
        "text": texto,
    }


def _formatear_fecha(fecha: Any) -> str:
    """Formatea la fecha igual que el export oficial (ISO 8601 sin zona)."""
    if not isinstance(fecha, datetime):
        return ""
    if fecha.tzinfo is not None:
        fecha = fecha.astimezone(timezone.utc).replace(tzinfo=None)
    return fecha.isoformat()


def _cabecera_export(nombre_chat: str, tipo_chat: str, id_chat: int | str) -> str:
    """Construye la apertura del JSON de export, hasta el inicio de ``messages``."""
    cabecera = {"name": nombre_chat, "type": tipo_chat, "id": id_chat}
    # Se recorta la llave de cierre para poder seguir añadiendo la clave
    # "messages" y sus elementos de uno en uno.
    return json.dumps(cabecera, ensure_ascii=False)[:-1] + ', "messages": ['


def escribir_export(
    ruta_destino: Path,
    nombre_chat: str,
    tipo_chat: str,
    id_chat: int | str,
    mensajes: Iterator[dict[str, Any]],
) -> int:
    """Escribe un archivo con el formato de export de Telegram, en streaming.

    Los mensajes se serializan de uno en uno en lugar de construir la lista
    completa: descargar un historial de cientos de miles de mensajes no debe
    consumir más memoria que analizarlo.

    Args:
        ruta_destino: Archivo JSON a escribir.
        nombre_chat: Nombre del chat.
        tipo_chat: Tipo de chat según Telegram.
        id_chat: Identificador del chat.
        mensajes: Iterador de mensajes ya convertidos.

    Returns:
        Número de mensajes escritos.
    """
    ruta_destino.parent.mkdir(parents=True, exist_ok=True)
    escritos = 0

    with ruta_destino.open("w", encoding="utf-8") as archivo:
        archivo.write(_cabecera_export(nombre_chat, tipo_chat, id_chat))

        for mensaje in mensajes:
            if escritos:
                archivo.write(",")
            archivo.write(json.dumps(mensaje, ensure_ascii=False))
            escritos += 1

        archivo.write("]}")

    return escritos


async def _iterar_mensajes_con_reintentos(
    cliente: Any,
    chat: Any,
    limite: int | None,
    consulta: str | None,
    filtro_servidor: Any,
    espera_maxima: int,
) -> Any:
    """Itera los mensajes de un chat sobreviviendo a los límites de Telegram.

    Recorrer todos los grupos y canales de una cuenta dispara ``FloodWait``
    con total seguridad: el servidor obliga a esperar antes de seguir. En
    lugar de abortar, se espera lo que pida Telegram y se reanuda desde el
    último mensaje recibido, sin repetir ni perder ninguno.

    Args:
        cliente: Cliente de Telethon autenticado.
        chat: Entidad del chat.
        limite: Máximo de mensajes (``None`` = todos).
        consulta: Búsqueda por texto en el servidor.
        filtro_servidor: Filtro por tipo de contenido, o ``None``.
        espera_maxima: Segundos máximos que se acepta esperar de una vez.

    Yields:
        Objetos ``Message`` de Telethon.
    """
    import asyncio

    from telethon.errors import FloodWaitError

    desplazamiento = 0
    restantes = limite

    while True:
        try:
            async for mensaje in cliente.iter_messages(
                chat,
                limit=restantes,
                offset_id=desplazamiento,
                search=consulta or None,
                filter=filtro_servidor,
            ):
                # Se recuerda el último identificador para poder reanudar
                # exactamente aquí si el servidor nos corta.
                desplazamiento = mensaje.id
                if restantes is not None:
                    restantes -= 1
                yield mensaje
            return
        except FloodWaitError as exc:
            espera = int(getattr(exc, "seconds", 0)) + 1
            if espera > espera_maxima:
                logger.error(
                    "Telegram exige esperar %ds, por encima del máximo de %ds. "
                    "Se deja este chat a medias y se continúa con el siguiente.",
                    espera,
                    espera_maxima,
                )
                return
            logger.warning(
                "Límite de Telegram alcanzado: esperando %ds antes de seguir...", espera
            )
            await asyncio.sleep(espera)


async def descargar_chat(
    cliente: Any,
    chat: Any,
    carpeta_destino: Path,
    limite: int | None = None,
    consulta: str | None = None,
    filtro: str = "todos",
    espera_maxima: int = 300,
) -> Path | None:
    """Descarga el historial de un chat y lo guarda como export JSON.

    Los mensajes se escriben a disco según llegan, no al final: descargar
    todos los grupos y canales de una cuenta puede suponer millones de
    mensajes, y acumularlos en una lista agotaría la memoria.

    Se escribe primero en un archivo temporal y se renombra al terminar. Así
    una descarga interrumpida no deja un JSON truncado en la carpeta de
    datos que el analizador intentaría leer después.

    Args:
        cliente: Cliente de Telethon ya autenticado.
        chat: Entidad de chat de Telethon.
        carpeta_destino: Carpeta donde escribir el archivo resultante.
        limite: Máximo de mensajes a descargar (``None`` = todos).
        consulta: Búsqueda por texto ejecutada en el servidor, si se indica.
        filtro: Clave de :data:`FILTROS_SERVIDOR` para acotar por tipo.
        espera_maxima: Segundos máximos que se acepta esperar por un
            ``FloodWait`` antes de pasar al siguiente chat.

    Returns:
        Ruta del archivo generado, o ``None`` si el chat no tenía mensajes
        con texto.
    """
    nombre_chat = getattr(chat, "title", None) or getattr(chat, "username", None) or "chat"
    id_chat = getattr(chat, "id", 0)
    ruta = carpeta_destino / f"{sanear_nombre_archivo(str(nombre_chat))}_{id_chat}.json"
    ruta_parcial = ruta.with_name(ruta.name + ".parcial")

    logger.info("Descargando '%s' (filtro: %s)...", nombre_chat, filtro)

    filtro_servidor = _obtener_filtro(filtro)
    carpeta_destino.mkdir(parents=True, exist_ok=True)
    escritos = 0
    descartados = 0

    try:
        with ruta_parcial.open("w", encoding="utf-8") as archivo:
            archivo.write(_cabecera_export(str(nombre_chat), "descarga_mtproto", id_chat))
            async for mensaje in _iterar_mensajes_con_reintentos(
                cliente, chat, limite, consulta, filtro_servidor, espera_maxima
            ):
                convertido = mensaje_a_formato_export(mensaje)
                if convertido is None:
                    descartados += 1
                    continue
                if escritos:
                    archivo.write(",")
                archivo.write(json.dumps(convertido, ensure_ascii=False))
                escritos += 1
            archivo.write("]}")
    except BaseException:
        # Incluye la cancelación por Ctrl+C: nunca dejar un archivo a medias
        # con extensión .json en la carpeta que analiza el programa.
        ruta_parcial.unlink(missing_ok=True)
        raise

    if not escritos:
        ruta_parcial.unlink(missing_ok=True)
        logger.warning("El chat '%s' no tiene mensajes con texto que guardar.", nombre_chat)
        return None

    ruta_parcial.replace(ruta)
    logger.info(
        "'%s': %d mensajes guardados en %s (%d sin texto omitidos).",
        nombre_chat,
        escritos,
        ruta.name,
        descartados,
    )
    return ruta


def incluir_dialogo(dialogo: Any, tipo: str) -> bool:
    """Decide si un diálogo entra en la selección según el tipo pedido.

    Telegram modela los supergrupos como canales, así que ``dialogo.is_channel``
    es cierto tanto para un canal de difusión como para un supergrupo. Para que
    "canales" signifique lo que el usuario espera —canales de difusión— hay que
    excluir explícitamente los que además son grupos.

    Args:
        dialogo: Objeto ``Dialog`` de Telethon.
        tipo: Una de las claves de :data:`TIPOS_CHAT`.

    Returns:
        ``True`` si el diálogo debe descargarse.
    """
    if tipo == "todos":
        return True
    es_grupo = bool(getattr(dialogo, "is_group", False))
    es_canal = bool(getattr(dialogo, "is_channel", False))
    es_usuario = bool(getattr(dialogo, "is_user", False))

    if tipo == "grupos":
        return es_grupo
    if tipo == "canales":
        return es_canal and not es_grupo
    if tipo == "privados":
        return es_usuario
    return True


async def descargar_historiales(
    credenciales: CredencialesTelegram,
    carpeta_destino: Path,
    carpeta_sesion: Path,
    chats: list[str] | None = None,
    limite: int | None = None,
    consulta: str | None = None,
    filtro: str = "todos",
    tipo: str = "todos",
    espera_maxima: int = 300,
    omitir_existentes: bool = False,
) -> list[Path]:
    """Descarga los historiales indicados y los deja listos para el análisis.

    Args:
        credenciales: Credenciales de la aplicación de Telegram.
        carpeta_destino: Carpeta de datos donde escribir los exports.
        carpeta_sesion: Carpeta donde guardar el archivo de sesión.
        chats: Nombres de usuario o identificadores de chat. Si es ``None``,
            se recorren todos los diálogos de la cuenta.
        limite: Máximo de mensajes por chat.
        consulta: Búsqueda por texto ejecutada en el servidor.
        filtro: Clave de :data:`FILTROS_SERVIDOR`.
        tipo: Clave de :data:`TIPOS_CHAT` para acotar qué diálogos incluir.
        espera_maxima: Segundos máximos de espera por ``FloodWait``.
        omitir_existentes: Si es ``True``, salta los chats cuyo archivo ya
            exista, permitiendo retomar una descarga interrumpida.

    Returns:
        Rutas de los archivos generados.

    Raises:
        ErrorDescarga: Si Telethon no está disponible o el tipo es inválido.
    """
    if not telethon_disponible():
        raise ErrorDescarga(
            "Telethon no está instalado. Ejecuta 'pip install telethon' "
            "o vuelve a lanzar el programa con --instalar-dependencias."
        )
    if tipo not in TIPOS_CHAT:
        raise ErrorDescarga(
            f"Tipo de chat desconocido: {tipo!r}. Válidos: {', '.join(TIPOS_CHAT)}"
        )

    from core.sesion import conectar

    generados: list[Path] = []
    fallidos = 0

    cliente = await conectar(credenciales, carpeta_sesion)
    try:
        entidades = await _resolver_chats(cliente, chats, tipo)
        if not entidades:
            logger.warning("No se encontró ningún chat que descargar.")
            return []

        logger.info("Chats a descargar: %d (tipo: %s)", len(entidades), tipo)

        for indice, entidad in enumerate(entidades, start=1):
            logger.info("[%d/%d]", indice, len(entidades))
            try:
                ruta = await descargar_chat(
                    cliente,
                    entidad,
                    carpeta_destino,
                    limite,
                    consulta,
                    filtro,
                    espera_maxima,
                )
            except KeyboardInterrupt:
                logger.warning(
                    "Descarga interrumpida. Se conservan los %d chats ya completados.",
                    len(generados),
                )
                break
            except Exception as exc:  # noqa: BLE001 - un chat fallido no aborta el resto
                fallidos += 1
                logger.error("Error descargando un chat (se continúa): %s", exc)
                continue
            if ruta is not None:
                generados.append(ruta)
    finally:
        await cliente.disconnect()

    if fallidos:
        logger.warning("Chats que no se pudieron descargar: %d. Revisa error.log.", fallidos)
    return generados


def describir_dialogo(dialogo: Any) -> dict[str, Any]:
    """Resume un diálogo en los datos que el usuario necesita para elegirlo.

    Returns:
        Diccionario con ``id``, ``nombre``, ``tipo`` y ``usuario``.
    """
    es_grupo = bool(getattr(dialogo, "is_group", False))
    es_canal = bool(getattr(dialogo, "is_channel", False))

    if es_grupo:
        tipo = "grupo"
    elif es_canal:
        tipo = "canal"
    elif getattr(dialogo, "is_user", False):
        tipo = "privado"
    else:
        tipo = "otro"

    entidad = getattr(dialogo, "entity", None)
    usuario = getattr(entidad, "username", None)

    return {
        "id": getattr(dialogo, "id", None),
        "nombre": getattr(dialogo, "name", None) or "(sin nombre)",
        "tipo": tipo,
        "usuario": f"@{usuario}" if usuario else "",
    }


async def listar_chats(
    credenciales: CredencialesTelegram,
    carpeta_sesion: Path,
    tipo: str = "todos",
) -> list[dict[str, Any]]:
    """Enumera los chats accesibles por la cuenta, para poder elegir cuáles usar.

    Los grupos privados no tienen nombre de usuario, así que la única forma
    de referirse a ellos es por su identificador numérico o por su título.
    Esta función es la que permite averiguarlos.

    Args:
        credenciales: Credenciales de la aplicación de Telegram.
        carpeta_sesion: Carpeta donde se guarda el archivo de sesión.
        tipo: Clave de :data:`TIPOS_CHAT` para acotar el listado.

    Returns:
        Lista de descripciones de chat, en el orden de la lista de diálogos.

    Raises:
        ErrorDescarga: Si Telethon no está disponible o el tipo es inválido.
    """
    if not telethon_disponible():
        raise ErrorDescarga(
            "Telethon no está instalado. Ejecuta 'pip install telethon' "
            "o vuelve a lanzar el programa con --instalar-dependencias."
        )
    if tipo not in TIPOS_CHAT:
        raise ErrorDescarga(
            f"Tipo de chat desconocido: {tipo!r}. Válidos: {', '.join(TIPOS_CHAT)}"
        )

    from core.sesion import conectar

    encontrados: list[dict[str, Any]] = []
    cliente = await conectar(credenciales, carpeta_sesion)

    try:
        async for dialogo in cliente.iter_dialogs():
            if incluir_dialogo(dialogo, tipo):
                encontrados.append(describir_dialogo(dialogo))
    finally:
        await cliente.disconnect()

    return encontrados


def coincide_con_dialogo(dialogo: Any, identificador: str) -> bool:
    """Indica si un diálogo corresponde al identificador escrito por el usuario.

    Acepta tres formas de referirse a un chat, de más a menos precisa:
    el identificador numérico, el nombre de usuario (con o sin ``@``) y una
    parte del título, sin distinguir mayúsculas ni acentos de posición.

    La búsqueda por título es imprescindible para los grupos privados, que
    carecen de nombre de usuario y cuyo identificador numérico nadie recuerda.

    Args:
        dialogo: Objeto ``Dialog`` de Telethon.
        identificador: Texto introducido por el usuario.

    Returns:
        ``True`` si el diálogo corresponde a ese identificador.
    """
    objetivo = identificador.strip().lstrip("@").casefold()
    if not objetivo:
        return False

    if str(getattr(dialogo, "id", "")) == identificador.strip():
        return True

    usuario = getattr(getattr(dialogo, "entity", None), "username", None)
    if isinstance(usuario, str) and usuario.casefold() == objetivo:
        return True

    nombre = getattr(dialogo, "name", None)
    return isinstance(nombre, str) and objetivo in nombre.casefold()


async def _resolver_chats(cliente: Any, chats: list[str] | None, tipo: str = "todos") -> list[Any]:
    """Obtiene las entidades a descargar: las pedidas, o todos los diálogos.

    Cuando se piden chats concretos se recorre la lista de diálogos y se
    emparejan por identificador, nombre de usuario o título. Recorrer los
    diálogos —en lugar de llamar a ``get_entity``— es lo que permite
    seleccionar grupos privados por su nombre, que es como los conoce el
    usuario.

    Args:
        cliente: Cliente de Telethon autenticado.
        chats: Identificadores concretos, o ``None`` para recorrer todo.
        tipo: Filtro por tipo de diálogo.

    Returns:
        Lista de entidades de chat, sin repeticiones.
    """
    entidades: list[Any] = []
    vistos: set[Any] = set()
    emparejados: set[str] = set()

    async for dialogo in cliente.iter_dialogs():
        if not incluir_dialogo(dialogo, tipo):
            continue

        if chats:
            coincidencias = [
                identificador for identificador in chats
                if coincide_con_dialogo(dialogo, identificador)
            ]
            if not coincidencias:
                continue
            emparejados.update(coincidencias)

        identidad = getattr(dialogo, "id", None)
        if identidad in vistos:
            continue
        vistos.add(identidad)
        entidades.append(dialogo.entity)

        if chats:
            logger.info(
                "Seleccionado: %s (%s)",
                getattr(dialogo, "name", "?"),
                describir_dialogo(dialogo)["tipo"],
            )

    for identificador in chats or []:
        if identificador not in emparejados:
            logger.error(
                "No se encontró ningún chat que coincida con '%s'. "
                "Usa --listar-chats para ver los nombres exactos.",
                identificador,
            )

    return entidades
