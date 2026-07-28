"""Parser de historiales de Telegram exportados en formato HTML.

Telegram genera un conjunto de archivos ``messages*.html`` con esta forma:

.. code-block:: html

    <div class="message default clearfix" id="message123">
      <div class="body">
        <div class="pull_right date details" title="26.01.2021 14:23:45 UTC-03:00">14:23</div>
        <div class="from_name">Peter</div>
        <div class="text">Hola <a href="https://ejemplo.com">enlace</a></div>
      </div>
    </div>

Los mensajes consecutivos del mismo remitente llevan la clase ``joined`` y
omiten ``from_name``: en ese caso se hereda el remitente del mensaje
anterior, igual que hace la propia interfaz de Telegram.

Se usa :mod:`html.parser` de la librería estándar, alimentado por bloques,
de modo que el consumo de memoria no depende del tamaño del archivo. El
módulo produce los mismos objetos :class:`core.models.Mensaje` que
:mod:`core.parser_json`, así que el motor de búsqueda es indiferente al
formato de origen.
"""

from __future__ import annotations

import logging
from html.parser import HTMLParser
from pathlib import Path
from typing import Iterator

from core.models import Mensaje

logger = logging.getLogger(__name__)

_USUARIO_DESCONOCIDO = "desconocido"

# Roles que puede desempeñar un <div> dentro del árbol del export.
_ROL_MENSAJE = "mensaje"
_ROL_SERVICIO = "servicio"
_ROL_NOMBRE = "from_name"
_ROL_TEXTO = "text"
_ROL_CABECERA = "page_header"


class _ExtractorMensajesHtml(HTMLParser):
    """Extrae mensajes de un export HTML de Telegram según se va alimentando.

    Los mensajes completados se acumulan en :attr:`mensajes_listos`, que el
    llamante vacía tras cada bloque para mantener acotada la memoria.
    """

    def __init__(self, archivo: str, chat_por_defecto: str) -> None:
        """Inicializa el extractor.

        Args:
            archivo: Nombre del archivo de origen, para trazabilidad.
            chat_por_defecto: Nombre de chat a usar si el HTML no declara uno.
        """
        super().__init__(convert_charrefs=True)
        self._archivo = archivo
        self.nombre_chat = chat_por_defecto
        self.mensajes_listos: list[Mensaje] = []

        self._pila_roles: list[str | None] = []
        self._en_mensaje = False
        self._en_cabecera = False
        self._cabecera_capturada = False

        self._captura: str | None = None
        self._profundidad_captura = 0
        self._buffer: list[str] = []

        self._id_actual = ""
        self._fecha_actual = ""
        self._usuario_actual = ""
        self._texto_actual = ""
        self._ultimo_usuario = _USUARIO_DESCONOCIDO

    # -- Callbacks de HTMLParser ------------------------------------------

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        """Procesa la apertura de una etiqueta."""
        if tag == "br" and self._captura is not None:
            self._buffer.append("\n")
            return

        if tag != "div":
            return

        atributos = dict(attrs)
        clases = set((atributos.get("class") or "").split())
        rol = self._determinar_rol(clases, atributos)
        self._pila_roles.append(rol)

    def handle_endtag(self, tag: str) -> None:
        """Procesa el cierre de una etiqueta."""
        if tag != "div" or not self._pila_roles:
            return

        rol = self._pila_roles.pop()

        if self._captura is not None and len(self._pila_roles) < self._profundidad_captura:
            self._cerrar_captura()

        if rol == _ROL_MENSAJE:
            self._emitir_mensaje()
        elif rol == _ROL_SERVICIO:
            self._en_mensaje = False
        elif rol == _ROL_CABECERA:
            self._en_cabecera = False

    def handle_data(self, data: str) -> None:
        """Acumula el texto cuando hay una captura activa."""
        if self._captura is not None:
            self._buffer.append(data)

    # -- Lógica interna ----------------------------------------------------

    def _determinar_rol(self, clases: set[str], atributos: dict[str, str | None]) -> str | None:
        """Asigna un rol al ``<div>`` que se acaba de abrir."""
        if "page_header" in clases and not self._cabecera_capturada:
            self._en_cabecera = True
            return _ROL_CABECERA

        if "message" in clases and not self._en_mensaje:
            if "service" in clases:
                self._en_mensaje = True
                return _ROL_SERVICIO
            self._iniciar_mensaje(atributos, clases)
            return _ROL_MENSAJE

        if self._en_mensaje:
            if "from_name" in clases:
                self._iniciar_captura(_ROL_NOMBRE)
                return _ROL_NOMBRE
            if "text" in clases:
                self._iniciar_captura(_ROL_TEXTO)
                return _ROL_TEXTO
            if "date" in clases:
                titulo = atributos.get("title")
                if titulo:
                    self._fecha_actual = titulo.strip()
                return None
            return None

        if self._en_cabecera and "text" in clases:
            self._iniciar_captura(_ROL_CABECERA)
            return None

        return None

    def _iniciar_mensaje(self, atributos: dict[str, str | None], clases: set[str]) -> None:
        """Prepara el estado para acumular un mensaje nuevo."""
        self._en_mensaje = True
        self._id_actual = (atributos.get("id") or "").replace("message", "").strip()
        self._fecha_actual = ""
        self._texto_actual = ""
        # Los mensajes "joined" no repiten el remitente: se hereda el anterior.
        self._usuario_actual = self._ultimo_usuario if "joined" in clases else ""

    def _iniciar_captura(self, destino: str) -> None:
        """Comienza a acumular texto para un destino concreto."""
        self._captura = destino
        self._profundidad_captura = len(self._pila_roles) + 1
        self._buffer = []

    def _cerrar_captura(self) -> None:
        """Vuelca el texto acumulado en el campo correspondiente."""
        texto = "".join(self._buffer).strip()
        destino = self._captura

        self._captura = None
        self._buffer = []

        if destino == _ROL_NOMBRE:
            if texto:
                self._usuario_actual = texto
                self._ultimo_usuario = texto
        elif destino == _ROL_TEXTO:
            self._texto_actual = texto
        elif destino == _ROL_CABECERA:
            if texto and not self._cabecera_capturada:
                self.nombre_chat = texto
                self._cabecera_capturada = True

    def _emitir_mensaje(self) -> None:
        """Cierra el mensaje en curso y lo añade a la lista de listos."""
        self._en_mensaje = False

        if not self._texto_actual:
            return

        self.mensajes_listos.append(
            Mensaje(
                archivo=self._archivo,
                fecha=self._fecha_actual,
                usuario=self._usuario_actual or _USUARIO_DESCONOCIDO,
                texto=self._texto_actual,
                id=self._id_actual,
                chat=self.nombre_chat,
            )
        )
        self._texto_actual = ""


def iterar_mensajes_html(ruta_html: Path, tam_bloque: int = 256 * 1024) -> Iterator[Mensaje]:
    """Extrae mensajes normalizados de un export HTML de Telegram.

    Args:
        ruta_html: Ruta al archivo HTML exportado por Telegram.
        tam_bloque: Caracteres leídos en cada operación de E/S.

    Yields:
        Instancias de :class:`core.models.Mensaje` por cada mensaje con texto.
    """
    extractor = _ExtractorMensajesHtml(ruta_html.name, ruta_html.stem)

    try:
        with ruta_html.open("r", encoding="utf-8", errors="replace") as archivo:
            while True:
                bloque = archivo.read(max(1024, tam_bloque))
                if not bloque:
                    break
                extractor.feed(bloque)
                if extractor.mensajes_listos:
                    yield from extractor.mensajes_listos
                    extractor.mensajes_listos.clear()

        extractor.close()
        yield from extractor.mensajes_listos
        extractor.mensajes_listos.clear()
    except OSError as exc:
        logger.error("No se pudo leer %s: %s", ruta_html, exc)
    except Exception as exc:  # noqa: BLE001 - un HTML corrupto no debe abortar todo
        logger.error("Error procesando el HTML %s: %s", ruta_html.name, exc)
