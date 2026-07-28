"""Parser de historiales de Telegram exportados en formato JSON.

Convierte los mensajes crudos que emite :mod:`core.json_stream` en objetos
:class:`core.models.Mensaje` normalizados.

Telegram guarda el campo ``text`` de dos formas distintas: como cadena
simple, o como lista de fragmentos cuando el mensaje contiene entidades
(enlaces, negritas, menciones, bloques de código...). Ambos casos se
aplanan aquí a texto plano, de modo que el motor de búsqueda trabaje
siempre contra el mismo contrato.

Todo el recorrido es perezoso: en ningún momento se mantiene en memoria más
de un mensaje a la vez.
"""

from __future__ import annotations

import logging
from pathlib import Path
from typing import Any, Iterable, Iterator

from core.json_stream import iterar_mensajes_crudos
from core.models import Mensaje

logger = logging.getLogger(__name__)

#: Tipos de mensaje que contienen texto de usuario. Los mensajes de servicio
#: ("fulano se unió al grupo") se descartan porque no son contenido buscable.
_TIPOS_CON_TEXTO = frozenset({"message"})

_USUARIO_DESCONOCIDO = "desconocido"


def iterar_archivos(carpeta_datos: Path, extensiones: Iterable[str]) -> Iterator[Path]:
    """Recorre recursivamente una carpeta buscando archivos de export.

    Args:
        carpeta_datos: Carpeta raíz donde el usuario coloca sus exports.
        extensiones: Extensiones aceptadas, en minúsculas y con punto inicial.

    Yields:
        Rutas a cada archivo encontrado, en orden alfabético estable.
    """
    if not carpeta_datos.is_dir():
        logger.error("La carpeta de datos no existe o no es una carpeta: %s", carpeta_datos)
        return

    extensiones_validas = {ext.lower() for ext in extensiones}

    try:
        # Se ordena para que la salida sea reproducible entre ejecuciones y
        # entre sistemas operativos (el orden de rglob no está garantizado).
        rutas = sorted(
            ruta
            for ruta in carpeta_datos.rglob("*")
            if ruta.is_file() and ruta.suffix.lower() in extensiones_validas
        )
    except OSError as exc:
        logger.error("Error recorriendo la carpeta de datos %s: %s", carpeta_datos, exc)
        return

    if not rutas:
        logger.warning(
            "No se encontró ningún archivo %s en %s",
            "/".join(sorted(extensiones_validas)),
            carpeta_datos,
        )

    yield from rutas


def iterar_mensajes(ruta_json: Path, tam_bloque: int = 256 * 1024) -> Iterator[Mensaje]:
    """Extrae mensajes normalizados de un export JSON de Telegram.

    Soporta tanto el export de un chat individual como el export completo de
    la cuenta, que agrupa varios chats en el mismo archivo.

    Args:
        ruta_json: Ruta al archivo JSON exportado por Telegram.
        tam_bloque: Caracteres leídos en cada operación de E/S.

    Yields:
        Instancias de :class:`Mensaje` por cada mensaje con texto.
    """
    nombre_archivo = ruta_json.name
    chat_por_defecto = ruta_json.stem
    encontrados = 0

    for nombre_chat, mensaje_crudo in iterar_mensajes_crudos(ruta_json, tam_bloque):
        mensaje = _normalizar_mensaje(
            mensaje_crudo,
            archivo=nombre_archivo,
            chat=nombre_chat or chat_por_defecto,
        )
        if mensaje is not None:
            encontrados += 1
            yield mensaje

    logger.debug("Mensajes con texto extraídos de %s: %d", nombre_archivo, encontrados)


def _normalizar_mensaje(mensaje_crudo: Any, archivo: str, chat: str) -> Mensaje | None:
    """Convierte un mensaje crudo de Telegram en un :class:`Mensaje`.

    Args:
        mensaje_crudo: Objeto tal como aparece en el array ``messages``.
        archivo: Nombre del archivo de origen.
        chat: Nombre del chat de origen.

    Returns:
        El mensaje normalizado, o ``None`` si no contiene texto buscable
        (mensajes de servicio, multimedia sin pie de foto, etc.).
    """
    if not isinstance(mensaje_crudo, dict):
        return None

    if mensaje_crudo.get("type") not in _TIPOS_CON_TEXTO:
        return None

    texto = _aplanar_texto(mensaje_crudo.get("text"))
    if not texto:
        return None

    return Mensaje(
        archivo=archivo,
        fecha=_texto_o_vacio(mensaje_crudo.get("date")),
        usuario=_nombre_usuario(mensaje_crudo),
        texto=texto,
        id=_texto_o_vacio(mensaje_crudo.get("id")),
        chat=chat,
    )


def _nombre_usuario(mensaje_crudo: dict[str, Any]) -> str:
    """Obtiene el nombre del remitente contemplando los campos alternativos.

    Telegram usa ``from`` en los mensajes normales y ``actor`` en los de
    servicio; ambos pueden venir a ``null`` (cuentas eliminadas o publicaciones
    anónimas de canal), en cuyo caso se recurre al identificador numérico.
    """
    for clave in ("from", "actor"):
        valor = mensaje_crudo.get(clave)
        if isinstance(valor, str) and valor.strip():
            return valor
    for clave in ("from_id", "actor_id"):
        valor = mensaje_crudo.get(clave)
        if valor not in (None, ""):
            return str(valor)
    return _USUARIO_DESCONOCIDO


def _texto_o_vacio(valor: Any) -> str:
    """Convierte un valor a cadena, devolviendo "" para ``None``.

    Evita que un campo ausente acabe escribiéndose como el literal "None"
    en los archivos de resultados.
    """
    return "" if valor is None else str(valor)


def _aplanar_texto(texto_crudo: Any) -> str:
    """Aplana el campo ``text`` de Telegram (cadena o lista) a texto plano.

    Args:
        texto_crudo: Valor del campo ``text`` tal como viene en el export.

    Returns:
        El texto plano resultante, o "" si no hay texto aprovechable.
    """
    if isinstance(texto_crudo, str):
        return texto_crudo

    if isinstance(texto_crudo, list):
        partes: list[str] = []
        for fragmento in texto_crudo:
            if isinstance(fragmento, str):
                partes.append(fragmento)
            elif isinstance(fragmento, dict):
                valor = fragmento.get("text")
                if isinstance(valor, str):
                    partes.append(valor)
        return "".join(partes)

    return ""
