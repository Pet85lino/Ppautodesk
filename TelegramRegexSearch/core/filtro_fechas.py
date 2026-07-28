"""Filtrado de mensajes por rango de fechas.

Telegram escribe las fechas de forma distinta según el formato de export:

* JSON: ``"date": "2024-01-01T10:00:00"`` (hora local, ISO 8601).
* HTML: ``title="01.01.2024 10:00:00 UTC+01:00"`` (día primero).

Este módulo entiende ambas y las normaliza a ``datetime`` sin zona horaria,
comparando siempre la hora tal como la ve el usuario en su cliente de
Telegram. Es lo coherente con lo que muestra el export y evita que un mismo
mensaje caiga a un lado u otro del corte según el formato de origen.

Las ventanas temporales están pensadas para poder pedir varias a la vez
(30, 60, 90, 160, 180 días). Como son concéntricas, un único recorrido de
los mensajes las resuelve todas: cada coincidencia se escribe en todas las
ventanas que la contienen.
"""

from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from datetime import datetime, timedelta

from core.models import Mensaje

logger = logging.getLogger(__name__)

#: Formatos de fecha admitidos, en orden de probabilidad.
_FORMATOS = (
    "%Y-%m-%dT%H:%M:%S",
    "%Y-%m-%d %H:%M:%S",
    "%d.%m.%Y %H:%M:%S",
    "%Y-%m-%d",
    "%d.%m.%Y",
)

#: Sufijo de zona horaria del export HTML (``UTC+01:00``), que se descarta
#: para comparar la hora local mostrada al usuario.
_SUFIJO_ZONA = re.compile(r"\s*UTC[+-]\d{2}:?\d{2}\s*$", re.IGNORECASE)

#: Etiqueta usada cuando no se solicita ninguna ventana concreta.
ETIQUETA_SIN_FILTRO = ""


def parsear_fecha(texto: str) -> datetime | None:
    """Convierte la fecha de un mensaje en un ``datetime`` sin zona horaria.

    Args:
        texto: Fecha tal como aparece en el export.

    Returns:
        El ``datetime`` correspondiente, o ``None`` si no se reconoce el
        formato o el campo viene vacío.
    """
    if not texto:
        return None

    limpio = _SUFIJO_ZONA.sub("", texto.strip())
    if not limpio:
        return None

    for formato in _FORMATOS:
        try:
            return datetime.strptime(limpio, formato)
        except ValueError:
            continue

    # Último recurso: marca de tiempo Unix (campo ``date_unixtime``).
    if limpio.isdigit():
        try:
            return datetime.fromtimestamp(int(limpio))
        except (OverflowError, OSError, ValueError):
            return None

    try:
        return datetime.fromisoformat(limpio)
    except ValueError:
        return None


@dataclass(frozen=True, slots=True)
class VentanaTemporal:
    """Rango de fechas en el que buscar coincidencias.

    Attributes:
        desde: Fecha mínima incluida, o ``None`` si no hay límite inferior.
        hasta: Fecha máxima incluida, o ``None`` si no hay límite superior.
        etiqueta: Nombre de la subcarpeta de resultados. Vacío significa
            escribir directamente en la carpeta de resultados.
    """

    desde: datetime | None = None
    hasta: datetime | None = None
    etiqueta: str = ETIQUETA_SIN_FILTRO

    @property
    def sin_limites(self) -> bool:
        """Indica si la ventana acepta cualquier fecha."""
        return self.desde is None and self.hasta is None

    def contiene(self, fecha: datetime | None) -> bool:
        """Indica si una fecha cae dentro de la ventana.

        Los mensajes cuya fecha no se puede interpretar se **incluyen**
        cuando hay filtro activo. Descartarlos en silencio sería peor: se
        perderían coincidencias reales por un problema de formato, que es
        justo lo contrario de lo que busca una herramienta de análisis.
        """
        if self.sin_limites:
            return True
        if fecha is None:
            return True
        if self.desde is not None and fecha < self.desde:
            return False
        return not (self.hasta is not None and fecha > self.hasta)


def construir_ventanas(
    dias: list[int] | None = None,
    desde: datetime | None = None,
    hasta: datetime | None = None,
    ahora: datetime | None = None,
) -> list[VentanaTemporal]:
    """Construye las ventanas temporales pedidas por el usuario.

    Args:
        dias: Antigüedades máximas en días (por ejemplo ``[30, 60, 90]``).
        desde: Límite inferior explícito.
        hasta: Límite superior explícito.
        ahora: Momento de referencia; por defecto, la fecha actual. Se puede
            fijar para que las pruebas sean deterministas.

    Returns:
        Lista de ventanas. Si no se pide nada, devuelve una única ventana sin
        límites, de modo que el resto del programa no necesite distinguir
        entre "con filtro" y "sin filtro".
    """
    referencia = ahora or datetime.now()

    if not dias:
        return [VentanaTemporal(desde=desde, hasta=hasta, etiqueta=ETIQUETA_SIN_FILTRO)]

    # Se ordenan y se eliminan repetidos para que las subcarpetas salgan de
    # menor a mayor y no se procese dos veces la misma ventana.
    valores = sorted({int(valor) for valor in dias if int(valor) > 0})
    if not valores:
        return [VentanaTemporal(desde=desde, hasta=hasta, etiqueta=ETIQUETA_SIN_FILTRO)]

    ventanas: list[VentanaTemporal] = []
    for valor in valores:
        limite = referencia - timedelta(days=valor)
        inicio = max(limite, desde) if desde is not None else limite
        etiqueta = f"ultimos_{valor}_dias" if len(valores) > 1 else ETIQUETA_SIN_FILTRO
        ventanas.append(VentanaTemporal(desde=inicio, hasta=hasta, etiqueta=etiqueta))

    return ventanas


def ventana_envolvente(ventanas: list[VentanaTemporal]) -> VentanaTemporal:
    """Devuelve la ventana más amplia que engloba a todas las indicadas.

    Sirve para descartar mensajes antes de aplicarles los regex: si un
    mensaje no cabe ni en la ventana más amplia, no hace falta evaluarlo
    contra ningún patrón.
    """
    if any(ventana.sin_limites for ventana in ventanas):
        return VentanaTemporal()

    limites_inferiores = [v.desde for v in ventanas if v.desde is not None]
    limites_superiores = [v.hasta for v in ventanas if v.hasta is not None]

    return VentanaTemporal(
        desde=min(limites_inferiores) if len(limites_inferiores) == len(ventanas) else None,
        hasta=max(limites_superiores) if len(limites_superiores) == len(ventanas) else None,
    )


class AsignadorVentanas:
    """Asigna cada mensaje a las ventanas temporales que lo contienen.

    Las coincidencias llegan agrupadas por mensaje, así que se memoriza la
    fecha del último analizado y se evita volver a interpretarla para cada
    una de sus coincidencias.
    """

    __slots__ = ("_ventanas", "_ultimo_mensaje", "_ultimas_etiquetas", "fechas_ilegibles")

    def __init__(self, ventanas: list[VentanaTemporal]) -> None:
        """Inicializa el asignador con las ventanas activas."""
        self._ventanas = ventanas
        self._ultimo_mensaje: Mensaje | None = None
        self._ultimas_etiquetas: tuple[str, ...] = ()
        self.fechas_ilegibles = 0

    def etiquetas_de(self, mensaje: Mensaje) -> tuple[str, ...]:
        """Devuelve las etiquetas de las ventanas que contienen el mensaje."""
        if mensaje is self._ultimo_mensaje:
            return self._ultimas_etiquetas

        fecha = parsear_fecha(mensaje.fecha)
        if fecha is None and mensaje.fecha:
            self.fechas_ilegibles += 1

        etiquetas = tuple(
            ventana.etiqueta for ventana in self._ventanas if ventana.contiene(fecha)
        )
        self._ultimo_mensaje = mensaje
        self._ultimas_etiquetas = etiquetas
        return etiquetas


def filtrar_mensajes(mensajes, ventana: VentanaTemporal):
    """Descarta los mensajes que quedan fuera de la ventana indicada.

    Aplicar el filtro antes de la búsqueda evita evaluar los patrones sobre
    mensajes que se iban a descartar de todos modos, que es donde está el
    grueso del coste en un historial largo.

    Args:
        mensajes: Iterable de :class:`core.models.Mensaje`.
        ventana: Ventana temporal a aplicar.

    Yields:
        Los mensajes que caen dentro de la ventana.
    """
    if ventana.sin_limites:
        yield from mensajes
        return

    for mensaje in mensajes:
        if ventana.contiene(parsear_fecha(mensaje.fecha)):
            yield mensaje
