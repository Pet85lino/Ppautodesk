"""Barra de progreso para consola, sin dependencias externas.

Pensada para funcionar igual en la consola de Windows (cmd/PowerShell) y en
la terminal de Pydroid 3:

* Solo usa caracteres ASCII, así que no depende de la página de códigos
  activa ni de que la fuente incluya glifos Unicode.
* Ajusta el ancho de la línea al de la terminal. Una línea más larga que la
  ventana provocaría un salto automático y, con ``\\r``, dejaría cientos de
  líneas basura en pantalla en lugar de una sola actualizándose.
* Si la salida no es una terminal (redirección a un archivo o a otro
  proceso), se desactiva sola para no ensuciar el destino con retornos de
  carro.
* Se refresca como mucho unas pocas veces por segundo: repintar en cada
  mensaje llegaría a ser más caro que la propia búsqueda.
"""

from __future__ import annotations

import shutil
import sys
import time
from types import TracebackType
from typing import TextIO

from utils.logger_setup import registrar_limpiador_de_progreso

#: Intervalo mínimo entre dos repintados, en segundos.
_INTERVALO_MINIMO_SEGUNDOS = 0.15

#: Ancho asumido cuando no se puede consultar el de la terminal.
_ANCHO_POR_DEFECTO = 80


class RastreadorProgreso:
    """Lleva la cuenta del avance del procesamiento y lo muestra en consola."""

    __slots__ = (
        "_actualizar_cada",
        "_salida",
        "_activo",
        "_tiempo_inicio",
        "_ultimo_repintado",
        "_longitud_ultima_linea",
        "archivos_procesados",
        "mensajes_procesados",
        "regex_evaluados",
        "coincidencias_encontradas",
    )

    def __init__(
        self,
        actualizar_cada_n_mensajes: int = 200,
        salida: TextIO | None = None,
        forzar_activo: bool | None = None,
    ) -> None:
        """Inicializa los contadores.

        Args:
            actualizar_cada_n_mensajes: Cada cuántos mensajes se evalúa si
                toca repintar la línea de progreso.
            salida: Flujo donde escribir (por defecto, la salida estándar).
            forzar_activo: Fuerza el modo activo o inactivo. Si es ``None``,
                se decide según si la salida es una terminal interactiva.
        """
        self._actualizar_cada = max(1, actualizar_cada_n_mensajes)
        self._salida = salida if salida is not None else sys.stdout
        self._activo = self._detectar_actividad() if forzar_activo is None else forzar_activo
        self._tiempo_inicio = time.monotonic()
        self._ultimo_repintado = 0.0
        self._longitud_ultima_linea = 0

        self.archivos_procesados = 0
        self.mensajes_procesados = 0
        self.regex_evaluados = 0
        self.coincidencias_encontradas = 0

    def _detectar_actividad(self) -> bool:
        """Determina si tiene sentido dibujar la barra en la salida actual."""
        try:
            return bool(self._salida.isatty())
        except (AttributeError, ValueError):
            return False

    # -- Contadores --------------------------------------------------------

    def registrar_archivo(self) -> None:
        """Contabiliza un archivo procesado y repinta la línea."""
        self.archivos_procesados += 1
        self.mostrar()

    def registrar_mensaje(self) -> None:
        """Contabiliza un mensaje procesado, repintando cada cierto número."""
        self.mensajes_procesados += 1
        if self.mensajes_procesados % self._actualizar_cada == 0:
            self.mostrar()

    def registrar_evaluaciones_regex(self, cantidad: int = 1) -> None:
        """Contabiliza ``cantidad`` evaluaciones de patrones.

        Se incrementa en bloque en lugar de una llamada por patrón: con
        cientos de miles de mensajes, la diferencia es apreciable.
        """
        self.regex_evaluados += cantidad

    def registrar_coincidencia(self, cantidad: int = 1) -> None:
        """Contabiliza ``cantidad`` coincidencias encontradas."""
        self.coincidencias_encontradas += cantidad

    # -- Métricas ----------------------------------------------------------

    def tiempo_transcurrido(self) -> float:
        """Segundos transcurridos desde que se creó el rastreador."""
        return time.monotonic() - self._tiempo_inicio

    def mensajes_por_segundo(self) -> float:
        """Velocidad media de procesamiento, en mensajes por segundo."""
        segundos = self.tiempo_transcurrido()
        return self.mensajes_procesados / segundos if segundos > 0 else 0.0

    def resumen(self) -> str:
        """Devuelve el texto de una sola línea con el estado actual."""
        return (
            f"Archivos: {self.archivos_procesados} | "
            f"Mensajes: {self.mensajes_procesados} | "
            f"Regex: {self.regex_evaluados} | "
            f"Coincidencias: {self.coincidencias_encontradas} | "
            f"Tiempo: {self._formatear_tiempo()} | "
            f"Msj/s: {self.mensajes_por_segundo():.0f}"
        )

    def _formatear_tiempo(self) -> str:
        """Formatea el tiempo transcurrido como ``MM:SS`` o ``HH:MM:SS``."""
        total = int(self.tiempo_transcurrido())
        horas, resto = divmod(total, 3600)
        minutos, segundos = divmod(resto, 60)
        if horas:
            return f"{horas:d}:{minutos:02d}:{segundos:02d}"
        return f"{minutos:02d}:{segundos:02d}"

    # -- Pintado -----------------------------------------------------------

    def mostrar(self, forzar: bool = False) -> None:
        """Repinta la línea de progreso si corresponde.

        Args:
            forzar: Repinta aunque no haya pasado el intervalo mínimo.
        """
        if not self._activo:
            return

        ahora = time.monotonic()
        if not forzar and (ahora - self._ultimo_repintado) < _INTERVALO_MINIMO_SEGUNDOS:
            return
        self._ultimo_repintado = ahora

        ancho = self._ancho_terminal()
        # Se reserva una columna: escribir en la última de algunas consolas de
        # Windows fuerza un salto de línea automático.
        linea = self.resumen()[: max(1, ancho - 1)]
        relleno = " " * max(0, self._longitud_ultima_linea - len(linea))
        self._longitud_ultima_linea = len(linea)

        self._escribir(f"\r{linea}{relleno}\r{linea}")

    def _ancho_terminal(self) -> int:
        """Devuelve el ancho actual de la terminal, con un valor de reserva."""
        try:
            return shutil.get_terminal_size((_ANCHO_POR_DEFECTO, 24)).columns
        except (OSError, ValueError):
            return _ANCHO_POR_DEFECTO

    def limpiar_linea(self) -> None:
        """Borra la línea de progreso actual.

        La invoca el handler de logging antes de escribir, para que los
        mensajes de log no queden mezclados con la barra.
        """
        if not self._activo or self._longitud_ultima_linea == 0:
            return
        self._escribir("\r" + " " * self._longitud_ultima_linea + "\r")
        self._longitud_ultima_linea = 0

    def finalizar(self) -> None:
        """Repinta por última vez y deja el cursor en una línea nueva."""
        if not self._activo:
            return
        self.mostrar(forzar=True)
        self._escribir("\n")
        self._longitud_ultima_linea = 0

    def _escribir(self, texto: str) -> None:
        """Escribe en la salida ignorando los errores propios de la consola.

        Una tubería cerrada (``python main.py | head``) o una consola que no
        admita algún carácter nunca deben tumbar el análisis en curso.
        """
        try:
            self._salida.write(texto)
            self._salida.flush()
        except (BrokenPipeError, ValueError, OSError, UnicodeEncodeError):
            self._activo = False

    # -- Gestor de contexto ------------------------------------------------

    def __enter__(self) -> "RastreadorProgreso":
        """Registra la barra para que el logging la limpie antes de escribir."""
        registrar_limpiador_de_progreso(self.limpiar_linea)
        return self

    def __exit__(
        self,
        tipo_excepcion: type[BaseException] | None,
        excepcion: BaseException | None,
        traza: TracebackType | None,
    ) -> None:
        """Cierra la línea de progreso y desregistra el limpiador."""
        registrar_limpiador_de_progreso(None)
        self.finalizar()
