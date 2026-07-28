"""Exportación de las coincidencias a archivos de texto.

Genera un archivo ``.txt`` por cada patrón dentro de la carpeta de
resultados, con un bloque legible por coincidencia.

Dos decisiones de diseño merecen explicación:

* **Los archivos se truncan en la primera escritura de cada ejecución.**
  Abrirlos siempre en modo añadir haría que los resultados de ejecuciones
  anteriores se mezclasen con los nuevos, algo especialmente confuso tras
  editar patrones.txt.
* **El número de archivos abiertos simultáneamente está acotado.** Con
  cientos de patrones, mantener un descriptor por cada uno agota el límite
  del sistema operativo (bajo en Android). Se usa una caché LRU que cierra
  los archivos usados hace más tiempo y los reabre en modo añadir si vuelven
  a necesitarse.
"""

from __future__ import annotations

import logging
from collections import OrderedDict
from pathlib import Path
from types import TracebackType
from typing import TextIO

from core.models import Coincidencia
from utils.filesystem import sanear_nombre_archivo

logger = logging.getLogger(__name__)

_SEPARADOR = "=" * 50
_EXTENSION = ".txt"


class ExportadorResultados:
    """Escribe las coincidencias en archivos TXT, uno por patrón."""

    __slots__ = (
        "_carpeta",
        "_codificacion",
        "_max_abiertos",
        "_abiertos",
        "_nombres_de_archivo",
        "_archivos_creados",
        "_rutas_ocupadas",
        "_escritas",
    )

    def __init__(
        self,
        carpeta_resultados: Path,
        codificacion: str = "utf-8",
        max_archivos_abiertos: int = 32,
    ) -> None:
        """Inicializa el exportador y garantiza que exista la carpeta destino.

        Args:
            carpeta_resultados: Carpeta donde se crearán los archivos.
            codificacion: Codificación de escritura de los archivos.
            max_archivos_abiertos: Máximo de descriptores mantenidos a la vez.

        Raises:
            OSError: Si la carpeta de resultados no se puede crear.
        """
        self._carpeta = carpeta_resultados
        self._codificacion = codificacion
        self._max_abiertos = max(1, max_archivos_abiertos)
        self._abiertos: OrderedDict[str, TextIO] = OrderedDict()
        self._nombres_de_archivo: dict[str, str] = {}
        self._archivos_creados: set[str] = set()
        self._rutas_ocupadas: set[str] = set()
        self._escritas = 0

        self._carpeta.mkdir(parents=True, exist_ok=True)

    @property
    def coincidencias_escritas(self) -> int:
        """Número de coincidencias volcadas a disco."""
        return self._escritas

    @property
    def archivos_generados(self) -> int:
        """Número de archivos de resultados creados en esta ejecución."""
        return len(self._archivos_creados)

    def exportar(self, coincidencia: Coincidencia) -> None:
        """Escribe una coincidencia en el archivo correspondiente a su patrón.

        Args:
            coincidencia: Coincidencia a registrar en disco.

        Raises:
            OSError: Si el archivo de destino no se puede abrir o escribir.
        """
        archivo = self._obtener_archivo(coincidencia.patron.nombre, coincidencia.patron.regex)
        archivo.write(self._formatear(coincidencia))
        self._escritas += 1

    def _formatear(self, coincidencia: Coincidencia) -> str:
        """Construye el bloque de texto correspondiente a una coincidencia."""
        mensaje = coincidencia.mensaje
        posicion = "n/d" if coincidencia.posicion < 0 else str(coincidencia.posicion)

        return (
            f"{_SEPARADOR}\n"
            f"Archivo: {mensaje.archivo}\n"
            f"Chat: {mensaje.chat}\n"
            f"Usuario: {mensaje.usuario}\n"
            f"Fecha: {mensaje.fecha}\n"
            f"ID mensaje: {mensaje.id}\n"
            f"\n"
            f"Regex: {coincidencia.patron.regex}\n"
            f"\n"
            f"Coincidencia: {coincidencia.texto_encontrado}\n"
            f"Posicion: {posicion}\n"
            f"Coincidencias en el mensaje: {coincidencia.total_en_mensaje}\n"
            f"\n"
            f"Mensaje completo:\n"
            f"{mensaje.texto}\n"
            f"{_SEPARADOR}\n\n"
        )

    def _obtener_archivo(self, nombre_patron: str, regex: str) -> TextIO:
        """Devuelve el archivo abierto para un patrón, abriéndolo si hace falta."""
        archivo = self._abiertos.get(nombre_patron)
        if archivo is not None:
            self._abiertos.move_to_end(nombre_patron)
            return archivo

        self._hacer_hueco()

        ruta = self._carpeta / self._nombre_de_archivo(nombre_patron)
        primera_vez = nombre_patron not in self._archivos_creados
        # La primera apertura de cada ejecución trunca; las reaperturas tras
        # un desalojo de la caché LRU deben conservar lo ya escrito.
        modo = "w" if primera_vez else "a"

        try:
            archivo = ruta.open(modo, encoding=self._codificacion, errors="replace")
        except OSError as exc:
            logger.error("No se pudo abrir el archivo de resultados %s: %s", ruta, exc)
            raise

        if primera_vez:
            self._archivos_creados.add(nombre_patron)
            archivo.write(f"# Patron: {nombre_patron}\n# Regex: {regex}\n\n")

        self._abiertos[nombre_patron] = archivo
        return archivo

    def _hacer_hueco(self) -> None:
        """Cierra el archivo usado hace más tiempo si se alcanzó el límite."""
        while len(self._abiertos) >= self._max_abiertos:
            _, archivo = self._abiertos.popitem(last=False)
            try:
                archivo.close()
            except OSError as exc:
                logger.error("Error cerrando un archivo de resultados: %s", exc)

    def _nombre_de_archivo(self, nombre_patron: str) -> str:
        """Calcula (y memoriza) el nombre de archivo asociado a un patrón.

        Dos patrones distintos pueden sanearse al mismo nombre —por ejemplo
        ``a/b`` y ``a\\b``—, lo que mezclaría sus resultados sin previo aviso.
        En ese caso se añade un sufijo numérico.
        """
        memorizado = self._nombres_de_archivo.get(nombre_patron)
        if memorizado is not None:
            return memorizado

        base = sanear_nombre_archivo(nombre_patron)
        candidato = f"{base}{_EXTENSION}"
        contador = 2
        while candidato.lower() in self._rutas_ocupadas:
            candidato = f"{base}_{contador}{_EXTENSION}"
            contador += 1

        if candidato != f"{base}{_EXTENSION}":
            logger.warning(
                "El nombre de archivo de '%s' colisionaba con otro patrón; se usa '%s'.",
                nombre_patron,
                candidato,
            )

        self._rutas_ocupadas.add(candidato.lower())
        self._nombres_de_archivo[nombre_patron] = candidato
        return candidato

    def cerrar(self) -> None:
        """Cierra todos los archivos que siga manteniendo abiertos."""
        for nombre, archivo in self._abiertos.items():
            try:
                archivo.close()
            except OSError as exc:
                logger.error("Error cerrando el archivo de resultados de '%s': %s", nombre, exc)
        self._abiertos.clear()

    def __enter__(self) -> "ExportadorResultados":
        """Permite usar el exportador como gestor de contexto."""
        return self

    def __exit__(
        self,
        tipo_excepcion: type[BaseException] | None,
        excepcion: BaseException | None,
        traza: TracebackType | None,
    ) -> None:
        """Cierra los archivos abiertos incluso si se produjo una excepción."""
        self.cerrar()
