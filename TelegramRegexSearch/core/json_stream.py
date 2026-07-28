"""Lector JSON incremental (streaming) construido solo con la librería estándar.

``json.load()`` construye en memoria el árbol completo del archivo. Un
export de Telegram puede ocupar cientos de megabytes, así que cargarlo
entero es inviable en Pydroid 3 y muy costoso en Windows.

Este módulo recorre el archivo por bloques y decodifica **un mensaje cada
vez**, manteniendo el consumo de memoria acotado por el tamaño de bloque
más el mensaje más grande del archivo, sin importar cuánto ocupe el JSON.

Funcionamiento
--------------
Un escáner avanza por el texto localizando pares ``"clave":``. Las cadenas
se decodifican con :meth:`json.JSONDecoder.raw_decode`, de modo que las
comillas escapadas y las secuencias ``\\uXXXX`` se interpretan
correctamente: un mensaje cuyo texto contenga literalmente ``"messages":``
nunca se confunde con la clave real, porque se consume como valor.

Cuando encuentra la clave ``messages`` seguida de un ``[``, decodifica los
elementos del array de uno en uno. La clave ``name`` que precede a cada
bloque de mensajes se recuerda como nombre del chat, lo que permite
soportar con el mismo código los dos formatos que exporta Telegram:

* Export de un solo chat: ``{"name": ..., "type": ..., "messages": [...]}``
* Export completo de la cuenta: ``{"chats": {"list": [{"name": ..., "messages": [...]}, ...]}}``
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any, Iterator, TextIO

logger = logging.getLogger(__name__)

#: Tope de crecimiento del buffer al decodificar un único valor JSON. Impide
#: que un archivo corrupto haga crecer la memoria sin límite: ningún mensaje
#: legítimo de Telegram se acerca a este tamaño.
_MAX_TAM_VALOR_BYTES = 32 * 1024 * 1024

_ESPACIOS = " \t\n\r"

_CLAVE_NOMBRE = "name"
_CLAVE_MENSAJES = "messages"


class ErrorJsonStream(Exception):
    """Error irrecuperable al recorrer un archivo JSON en streaming."""


class _EscanerJson:
    """Escáner incremental sobre un archivo JSON abierto en modo texto.

    Mantiene una ventana deslizante del contenido: solo conserva en memoria
    la parte del archivo que aún no ha consumido.
    """

    __slots__ = ("_archivo", "_tam_bloque", "_buffer", "_pos", "_fin_archivo", "_decoder")

    def __init__(self, archivo: TextIO, tam_bloque: int) -> None:
        """Inicializa el escáner.

        Args:
            archivo: Archivo de texto ya abierto y posicionado al principio.
            tam_bloque: Cantidad de caracteres leídos en cada operación de E/S.
        """
        self._archivo = archivo
        self._tam_bloque = max(1024, tam_bloque)
        self._buffer = ""
        self._pos = 0
        self._fin_archivo = False
        self._decoder = json.JSONDecoder()

    # -- Gestión del buffer ------------------------------------------------

    def _leer_bloque(self) -> bool:
        """Añade un bloque más al buffer. Devuelve ``False`` si ya no hay datos."""
        if self._fin_archivo:
            return False
        bloque = self._archivo.read(self._tam_bloque)
        if not bloque:
            self._fin_archivo = True
            return False
        self._buffer += bloque
        return True

    def _compactar(self) -> None:
        """Descarta la parte ya consumida del buffer para liberar memoria."""
        if self._pos >= self._tam_bloque:
            self._buffer = self._buffer[self._pos :]
            self._pos = 0

    def _asegurar(self, cantidad: int = 1) -> bool:
        """Garantiza que haya al menos ``cantidad`` caracteres sin consumir."""
        while len(self._buffer) - self._pos < cantidad:
            if not self._leer_bloque():
                return False
        return True

    def _caracter_actual(self) -> str | None:
        """Devuelve el carácter en la posición actual, o ``None`` al final."""
        if not self._asegurar(1):
            return None
        return self._buffer[self._pos]

    def _saltar_espacios(self) -> None:
        """Avanza la posición hasta el siguiente carácter no blanco."""
        while True:
            caracter = self._caracter_actual()
            if caracter is None or caracter not in _ESPACIOS:
                return
            self._pos += 1

    # -- Decodificación ----------------------------------------------------

    def _decodificar_valor(self) -> Any:
        """Decodifica el valor JSON que empieza en la posición actual.

        Si el buffer contiene un valor truncado, sigue leyendo bloques hasta
        completarlo.

        Raises:
            ErrorJsonStream: Si el valor está mal formado o supera el tamaño
                máximo admitido.
        """
        self._compactar()
        while True:
            try:
                valor, fin = self._decoder.raw_decode(self._buffer, self._pos)
            except ValueError:
                # Puede tratarse de un valor incompleto (falta leer más) o de
                # un JSON realmente inválido; solo lo sabemos al llegar al final.
                if len(self._buffer) - self._pos > _MAX_TAM_VALOR_BYTES:
                    raise ErrorJsonStream(
                        "Valor JSON descomunal o archivo corrupto: se superaron "
                        f"{_MAX_TAM_VALOR_BYTES} caracteres sin cerrar el valor."
                    ) from None
                if not self._leer_bloque():
                    raise ErrorJsonStream(
                        "JSON mal formado o truncado cerca del final del archivo."
                    ) from None
                continue
            self._pos = fin
            return valor

    # -- Recorrido ---------------------------------------------------------

    def iterar_mensajes(self) -> Iterator[tuple[str | None, dict[str, Any]]]:
        """Recorre el archivo emitiendo los mensajes que encuentre.

        Yields:
            Tuplas ``(nombre_chat, mensaje_crudo)``. El nombre puede ser
            ``None`` si el export no lo declara.
        """
        nombre_chat: str | None = None

        while True:
            caracter = self._caracter_actual()
            if caracter is None:
                return

            if caracter != '"':
                self._pos += 1
                continue

            # Toda cadena se decodifica por completo; así las comillas
            # escapadas dentro del texto de un mensaje nunca desincronizan
            # el escáner.
            cadena = self._decodificar_valor()
            self._saltar_espacios()

            if self._caracter_actual() != ":":
                continue  # Era un valor, no una clave.

            self._pos += 1
            self._saltar_espacios()

            if cadena == _CLAVE_NOMBRE:
                if self._caracter_actual() == '"':
                    valor = self._decodificar_valor()
                    if isinstance(valor, str) and valor.strip():
                        nombre_chat = valor
            elif cadena == _CLAVE_MENSAJES:
                if self._caracter_actual() == "[":
                    yield from self._iterar_array_de_mensajes(nombre_chat)

    def _iterar_array_de_mensajes(
        self, nombre_chat: str | None
    ) -> Iterator[tuple[str | None, dict[str, Any]]]:
        """Emite uno a uno los elementos del array de mensajes actual."""
        self._pos += 1  # Consume el '['
        self._saltar_espacios()

        if self._caracter_actual() == "]":
            self._pos += 1
            return

        while True:
            elemento = self._decodificar_valor()
            if isinstance(elemento, dict):
                yield nombre_chat, elemento
            else:
                logger.debug("Elemento ignorado en 'messages': no es un objeto JSON.")

            self._saltar_espacios()
            caracter = self._caracter_actual()

            if caracter == ",":
                self._pos += 1
                self._saltar_espacios()
                continue
            if caracter == "]":
                self._pos += 1
                return
            raise ErrorJsonStream(
                f"Se esperaba ',' o ']' dentro del array de mensajes y se encontró {caracter!r}."
            )


def iterar_mensajes_crudos(
    ruta_json: Path, tam_bloque: int = 256 * 1024
) -> Iterator[tuple[str | None, dict[str, Any]]]:
    """Recorre un export JSON de Telegram emitiendo sus mensajes en streaming.

    Los errores de lectura o de formato se registran y detienen únicamente
    el archivo afectado, sin abortar el resto del procesamiento.

    Args:
        ruta_json: Ruta al archivo JSON exportado por Telegram.
        tam_bloque: Caracteres leídos en cada operación de E/S.

    Yields:
        Tuplas ``(nombre_chat, mensaje_crudo)`` en el orden del archivo.
    """
    try:
        # ``newline=""`` evita la traducción de saltos de línea, que en
        # Windows desplazaría los índices respecto al contenido real.
        with ruta_json.open("r", encoding="utf-8", errors="replace", newline="") as archivo:
            escaner = _EscanerJson(archivo, tam_bloque)
            yield from escaner.iterar_mensajes()
    except ErrorJsonStream as exc:
        logger.error("Archivo JSON inválido, se omite (%s): %s", ruta_json.name, exc)
    except OSError as exc:
        logger.error("No se pudo leer %s: %s", ruta_json, exc)
