"""Estructuras de datos compartidas por todo el proyecto.

Centralizar aquí los ``dataclass`` evita dependencias cruzadas entre
capas: el exportador necesita conocer :class:`Coincidencia` sin tener que
importar el motor de búsqueda, y los parsers (JSON/HTML) comparten el
mismo contrato :class:`Mensaje` sin depender el uno del otro.

Todas las estructuras son inmutables (``frozen=True``) y usan ``slots``
para reducir el consumo de memoria, algo relevante cuando se procesan
cientos de miles de mensajes en dispositivos con RAM limitada.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Literal

#: Modos de búsqueda soportados, equivalentes a los métodos de ``re.Pattern``.
ModoBusqueda = Literal["search", "findall", "finditer"]

#: Tupla con los modos válidos, para validaciones en tiempo de ejecución.
MODOS_VALIDOS: tuple[str, ...] = ("search", "findall", "finditer")


@dataclass(frozen=True, slots=True)
class Mensaje:
    """Mensaje normalizado, independiente del formato de origen (JSON/HTML).

    Attributes:
        archivo: Nombre del archivo de origen.
        fecha: Fecha/hora del mensaje tal como aparece en el export.
        usuario: Nombre del remitente ("desconocido" si el export no lo indica).
        texto: Texto plano del mensaje (entidades aplanadas a texto).
        id: Identificador del mensaje dentro del chat.
        chat: Nombre del chat/conversación de origen.
    """

    archivo: str
    fecha: str
    usuario: str
    texto: str
    id: str
    chat: str


@dataclass(frozen=True, slots=True)
class PatronRegex:
    """Patrón regex ya validado y compilado.

    Attributes:
        nombre: Nombre identificador del patrón (usado en nombres de archivo).
        regex: Cadena original del regex tal como aparece en patrones.txt.
        compilado: Objeto ``re.Pattern`` compilado, listo para usarse.
        numero_linea: Línea de patrones.txt donde se definió el patrón.
    """

    nombre: str
    regex: str
    compilado: re.Pattern[str]
    numero_linea: int


@dataclass(frozen=True, slots=True)
class Coincidencia:
    """Coincidencia encontrada entre un patrón y un mensaje.

    Attributes:
        patron: Patrón regex que produjo la coincidencia.
        mensaje: Mensaje donde se encontró la coincidencia.
        texto_encontrado: Fragmento de texto que coincidió con el patrón.
        posicion: Índice de inicio de la coincidencia dentro del texto del
            mensaje, o ``-1`` cuando el modo de búsqueda no aporta posición
            (caso de ``findall``).
        total_en_mensaje: Cantidad total de coincidencias de este patrón
            dentro del mismo mensaje. Vale ``1`` en modo ``search``.
    """

    patron: PatronRegex
    mensaje: Mensaje
    texto_encontrado: str
    posicion: int
    total_en_mensaje: int = 1

    def clave_unica(self) -> tuple[str, str, str, str, int]:
        """Devuelve la clave usada para detectar coincidencias duplicadas.

        Dos coincidencias se consideran la misma si provienen del mismo
        patrón, el mismo chat, el mismo mensaje, con idéntico texto
        encontrado y en la misma posición.
        """
        return (
            self.patron.nombre,
            self.mensaje.chat,
            self.mensaje.id,
            self.texto_encontrado,
            self.posicion,
        )
