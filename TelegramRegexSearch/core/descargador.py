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
        cabecera = {"name": nombre_chat, "type": tipo_chat, "id": id_chat}
        archivo.write(json.dumps(cabecera, ensure_ascii=False)[:-1])
        archivo.write(', "messages": [')

        for mensaje in mensajes:
            if escritos:
                archivo.write(",")
            archivo.write(json.dumps(mensaje, ensure_ascii=False))
            escritos += 1

        archivo.write("]}")

    return escritos


async def descargar_chat(
    cliente: Any,
    chat: Any,
    carpeta_destino: Path,
    limite: int | None = None,
    consulta: str | None = None,
    filtro: str = "todos",
) -> Path | None:
    """Descarga el historial de un chat y lo guarda como export JSON.

    Args:
        cliente: Cliente de Telethon ya autenticado.
        chat: Entidad de chat de Telethon.
        carpeta_destino: Carpeta donde escribir el archivo resultante.
        limite: Máximo de mensajes a descargar (``None`` = todos).
        consulta: Búsqueda por texto ejecutada en el servidor, si se indica.
        filtro: Clave de :data:`FILTROS_SERVIDOR` para acotar por tipo.

    Returns:
        Ruta del archivo generado, o ``None`` si el chat no tenía mensajes
        con texto.
    """
    nombre_chat = getattr(chat, "title", None) or getattr(chat, "username", None) or "chat"
    id_chat = getattr(chat, "id", 0)
    ruta = carpeta_destino / f"{sanear_nombre_archivo(str(nombre_chat))}_{id_chat}.json"

    logger.info("Descargando '%s' (filtro: %s)...", nombre_chat, filtro)

    filtro_servidor = _obtener_filtro(filtro)
    mensajes: list[dict[str, Any]] = []
    descartados = 0

    async for mensaje in cliente.iter_messages(
        chat, limit=limite, search=consulta or None, filter=filtro_servidor
    ):
        convertido = mensaje_a_formato_export(mensaje)
        if convertido is None:
            descartados += 1
            continue
        mensajes.append(convertido)

    if not mensajes:
        logger.warning("El chat '%s' no tiene mensajes con texto que guardar.", nombre_chat)
        return None

    escritos = escribir_export(ruta, str(nombre_chat), "descarga_mtproto", id_chat, iter(mensajes))
    logger.info(
        "'%s': %d mensajes guardados en %s (%d sin texto omitidos).",
        nombre_chat,
        escritos,
        ruta.name,
        descartados,
    )
    return ruta


async def descargar_historiales(
    credenciales: CredencialesTelegram,
    carpeta_destino: Path,
    carpeta_sesion: Path,
    chats: list[str] | None = None,
    limite: int | None = None,
    consulta: str | None = None,
    filtro: str = "todos",
) -> list[Path]:
    """Descarga los historiales indicados y los deja listos para el análisis.

    Args:
        credenciales: Credenciales de la aplicación de Telegram.
        carpeta_destino: Carpeta de datos donde escribir los exports.
        carpeta_sesion: Carpeta donde guardar el archivo de sesión.
        chats: Nombres de usuario o identificadores de chat. Si es ``None``,
            se descargan todos los diálogos de la cuenta.
        limite: Máximo de mensajes por chat.
        consulta: Búsqueda por texto ejecutada en el servidor.
        filtro: Clave de :data:`FILTROS_SERVIDOR`.

    Returns:
        Rutas de los archivos generados.

    Raises:
        ErrorDescarga: Si Telethon no está disponible o falla la conexión.
    """
    if not telethon_disponible():
        raise ErrorDescarga(
            "Telethon no está instalado. Ejecuta 'pip install telethon' "
            "o vuelve a lanzar el programa con --instalar-dependencias."
        )

    from telethon import TelegramClient

    carpeta_sesion.mkdir(parents=True, exist_ok=True)
    ruta_sesion = carpeta_sesion / "telegram"

    generados: list[Path] = []

    async with TelegramClient(
        str(ruta_sesion), credenciales.api_id, credenciales.api_hash
    ) as cliente:
        entidades = await _resolver_chats(cliente, chats)
        if not entidades:
            logger.warning("No se encontró ningún chat que descargar.")
            return []

        logger.info("Chats a descargar: %d", len(entidades))
        for entidad in entidades:
            try:
                ruta = await descargar_chat(
                    cliente, entidad, carpeta_destino, limite, consulta, filtro
                )
            except Exception as exc:  # noqa: BLE001 - un chat fallido no aborta el resto
                logger.error("Error descargando un chat: %s", exc)
                continue
            if ruta is not None:
                generados.append(ruta)

    return generados


async def _resolver_chats(cliente: Any, chats: list[str] | None) -> list[Any]:
    """Obtiene las entidades de los chats pedidos, o todos los diálogos."""
    if not chats:
        return [dialogo.entity async for dialogo in cliente.iter_dialogs()]

    entidades = []
    for identificador in chats:
        try:
            entidades.append(await cliente.get_entity(identificador))
        except Exception as exc:  # noqa: BLE001 - un chat inválido no aborta el resto
            logger.error("No se pudo resolver el chat '%s': %s", identificador, exc)
    return entidades
