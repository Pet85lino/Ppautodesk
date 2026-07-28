"""Motor de búsqueda: aplica patrones regex sobre mensajes normalizados.

Modos soportados, equivalentes a los métodos de :class:`re.Pattern`:

* ``search``: primera coincidencia de cada patrón en cada mensaje.
* ``findall``: todas las coincidencias, sin posición.
* ``finditer``: todas las coincidencias, con su posición dentro del texto.

El recorrido es en streaming: los mensajes se consumen de un iterador y las
coincidencias se emiten según se encuentran, sin acumular resultados en
memoria. El único estado que crece es el registro de claves para evitar
duplicados, y está acotado por :data:`_MAX_CLAVES_DUPLICADOS`.
"""

from __future__ import annotations

import logging
import time
from typing import Any, Iterable, Iterator, Sequence

from core.models import Coincidencia, Mensaje, ModoBusqueda, PatronRegex

logger = logging.getLogger(__name__)

#: Tope de claves memorizadas para descartar duplicados. Al superarlo se
#: desactiva la deduplicación en lugar de agotar la memoria del dispositivo.
_MAX_CLAVES_DUPLICADOS = 200_000

#: Una evaluación que supere este tiempo delata un patrón con retroceso
#: catastrófico (por ejemplo ``(a+)+$``), que puede colgar el proceso.
_UMBRAL_PATRON_LENTO_SEGUNDOS = 0.5

#: Separador usado al aplanar los grupos que devuelve ``findall``.
_SEPARADOR_GRUPOS = " | "


class MotorBusqueda:
    """Aplica un conjunto de patrones compilados sobre un flujo de mensajes."""

    __slots__ = (
        "_patrones",
        "_modo",
        "_evitar_duplicados",
        "_claves_vistas",
        "_dedup_activa",
        "_tiempo_por_patron",
        "_patrones_lentos_avisados",
        "evaluaciones",
        "coincidencias_emitidas",
        "duplicados_descartados",
    )

    def __init__(
        self,
        patrones: Sequence[PatronRegex],
        modo: ModoBusqueda = "search",
        evitar_duplicados: bool = True,
    ) -> None:
        """Inicializa el motor.

        Args:
            patrones: Patrones ya compilados por :mod:`core.regex_loader`.
                Se reutilizan tal cual durante toda la ejecución: nunca se
                recompilan por mensaje.
            modo: Estrategia de búsqueda a aplicar.
            evitar_duplicados: Si es ``True``, una misma coincidencia (mismo
                patrón, chat, mensaje, texto y posición) se emite una sola vez.
        """
        self._patrones = tuple(patrones)
        self._modo = modo
        self._evitar_duplicados = evitar_duplicados
        self._claves_vistas: set[int] = set()
        self._dedup_activa = evitar_duplicados
        self._tiempo_por_patron: dict[str, float] = {}
        self._patrones_lentos_avisados: set[str] = set()
        self.evaluaciones = 0
        self.coincidencias_emitidas = 0
        self.duplicados_descartados = 0

    @property
    def cantidad_patrones(self) -> int:
        """Número de patrones activos en el motor."""
        return len(self._patrones)

    def buscar(self, mensajes: Iterable[Mensaje]) -> Iterator[Coincidencia]:
        """Aplica todos los patrones sobre todos los mensajes.

        Args:
            mensajes: Iterable de mensajes normalizados (idealmente un generador).

        Yields:
            Instancias de :class:`Coincidencia` a medida que se encuentran.
        """
        if not self._patrones:
            logger.warning("No hay patrones cargados: no se realizará ninguna búsqueda.")
            return

        for mensaje in mensajes:
            for patron in self._patrones:
                for coincidencia in self._buscar_en_mensaje(mensaje, patron):
                    if self._es_duplicada(coincidencia):
                        self.duplicados_descartados += 1
                        continue
                    self.coincidencias_emitidas += 1
                    yield coincidencia

    def _buscar_en_mensaje(self, mensaje: Mensaje, patron: PatronRegex) -> list[Coincidencia]:
        """Aplica un patrón sobre un mensaje y devuelve sus coincidencias.

        Se devuelve una lista (y no un generador) porque el número total de
        coincidencias forma parte de cada resultado y solo se conoce tras
        recorrerlas todas. La lista está acotada por el tamaño de un único
        mensaje, así que no compromete el consumo de memoria.
        """
        self.evaluaciones += 1
        inicio = time.perf_counter()
        try:
            crudas = self._ejecutar(patron, mensaje.texto)
        except (RuntimeError, RecursionError, MemoryError) as exc:
            # Patrones con anidamiento extremo pueden agotar la pila del
            # motor de expresiones regulares; se descarta el mensaje, no la
            # ejecución completa.
            logger.error(
                "Fallo evaluando el patrón '%s' (línea %d) en el mensaje %s del chat '%s': %s",
                patron.nombre,
                patron.numero_linea,
                mensaje.id or "?",
                mensaje.chat,
                exc,
            )
            return []
        finally:
            self._registrar_tiempo(patron, time.perf_counter() - inicio)

        total = len(crudas)
        return [
            Coincidencia(
                patron=patron,
                mensaje=mensaje,
                texto_encontrado=texto,
                posicion=posicion,
                total_en_mensaje=total,
            )
            for texto, posicion in crudas
        ]

    def _ejecutar(self, patron: PatronRegex, texto: str) -> list[tuple[str, int]]:
        """Ejecuta el patrón según el modo y normaliza el resultado.

        Returns:
            Lista de tuplas ``(texto_encontrado, posicion)``, donde la
            posición es ``-1`` cuando el modo no la proporciona.
        """
        if self._modo == "search":
            encontrado = patron.compilado.search(texto)
            return [] if encontrado is None else [(encontrado.group(0), encontrado.start())]

        if self._modo == "finditer":
            return [(m.group(0), m.start()) for m in patron.compilado.finditer(texto)]

        if self._modo == "findall":
            return [(_aplanar_resultado(bruto), -1) for bruto in patron.compilado.findall(texto)]

        # Inalcanzable con una configuración validada; se deja explícito para
        # que un modo nuevo no falle en silencio devolviendo cero resultados.
        raise ValueError(f"Modo de búsqueda no soportado: {self._modo!r}")

    def _registrar_tiempo(self, patron: PatronRegex, transcurrido: float) -> None:
        """Acumula el tiempo consumido por un patrón y avisa si es patológico."""
        self._tiempo_por_patron[patron.nombre] = (
            self._tiempo_por_patron.get(patron.nombre, 0.0) + transcurrido
        )
        if (
            transcurrido >= _UMBRAL_PATRON_LENTO_SEGUNDOS
            and patron.nombre not in self._patrones_lentos_avisados
        ):
            self._patrones_lentos_avisados.add(patron.nombre)
            logger.warning(
                "El patrón '%s' (línea %d) tardó %.2fs en un solo mensaje. "
                "Suele indicar retroceso catastrófico: revisa anidamientos "
                "del tipo (a+)+ y acota los cuantificadores.",
                patron.nombre,
                patron.numero_linea,
                transcurrido,
            )

    def _es_duplicada(self, coincidencia: Coincidencia) -> bool:
        """Indica si la coincidencia ya se había emitido antes."""
        if not self._dedup_activa:
            return False

        clave = hash(coincidencia.clave_unica())
        if clave in self._claves_vistas:
            return True

        if len(self._claves_vistas) >= _MAX_CLAVES_DUPLICADOS:
            self._dedup_activa = False
            self._claves_vistas.clear()
            logger.warning(
                "Se superaron %d coincidencias únicas: se desactiva el filtro de "
                "duplicados para no agotar la memoria.",
                _MAX_CLAVES_DUPLICADOS,
            )
            return False

        self._claves_vistas.add(clave)
        return False

    def estadisticas(self) -> dict[str, Any]:
        """Devuelve un resumen de la ejecución para el log final."""
        return {
            "patrones": len(self._patrones),
            "evaluaciones": self.evaluaciones,
            "coincidencias": self.coincidencias_emitidas,
            "duplicados_descartados": self.duplicados_descartados,
            "patron_mas_lento": self.patron_mas_lento(),
        }

    def patron_mas_lento(self) -> tuple[str, float] | None:
        """Devuelve el patrón que más tiempo consumió y sus segundos totales."""
        if not self._tiempo_por_patron:
            return None
        nombre = max(self._tiempo_por_patron, key=lambda clave: self._tiempo_por_patron[clave])
        return nombre, self._tiempo_por_patron[nombre]


def _aplanar_resultado(bruto: Any) -> str:
    """Convierte un elemento devuelto por ``findall`` en texto legible.

    ``findall`` devuelve tuplas cuando el patrón tiene varios grupos de
    captura. Volcarlas con ``str()`` produciría basura como ``('a', 'b')``
    en el archivo de resultados, así que se unen los grupos no vacíos.
    """
    if isinstance(bruto, str):
        return bruto
    if isinstance(bruto, tuple):
        return _SEPARADOR_GRUPOS.join(parte for parte in bruto if parte)
    return str(bruto)


def buscar(
    mensajes: Iterable[Mensaje],
    patrones: Sequence[PatronRegex],
    modo: ModoBusqueda = "search",
    evitar_duplicados: bool = True,
) -> Iterator[Coincidencia]:
    """Atajo funcional para una búsqueda puntual sin gestionar el motor.

    Args:
        mensajes: Iterable de mensajes normalizados.
        patrones: Patrones ya compilados.
        modo: Estrategia de búsqueda.
        evitar_duplicados: Descarta coincidencias repetidas si es ``True``.

    Yields:
        Instancias de :class:`Coincidencia`.
    """
    yield from MotorBusqueda(patrones, modo, evitar_duplicados).buscar(mensajes)
