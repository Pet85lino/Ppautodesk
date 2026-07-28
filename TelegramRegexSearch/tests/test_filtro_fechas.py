"""Pruebas del filtrado por rango de fechas."""

from __future__ import annotations

import logging
import unittest
from datetime import datetime, timedelta

from core.filtro_fechas import (
    AsignadorVentanas,
    VentanaTemporal,
    construir_ventanas,
    filtrar_mensajes,
    parsear_fecha,
    ventana_envolvente,
)
from core.models import Mensaje

_AHORA = datetime(2024, 6, 15, 12, 0, 0)


def _mensaje(fecha: str, identificador: str = "1") -> Mensaje:
    """Construye un mensaje con la fecha indicada."""
    return Mensaje(
        archivo="export.json",
        fecha=fecha,
        usuario="Ana",
        texto="texto",
        id=identificador,
        chat="Chat",
    )


class TestParsearFecha(unittest.TestCase):
    """Verifica el reconocimiento de los formatos de fecha de Telegram."""

    def test_formato_iso_del_export_json(self) -> None:
        self.assertEqual(
            parsear_fecha("2024-01-15T10:30:00"), datetime(2024, 1, 15, 10, 30, 0)
        )

    def test_formato_del_export_html_con_zona_horaria(self) -> None:
        self.assertEqual(
            parsear_fecha("15.01.2024 10:30:00 UTC+01:00"), datetime(2024, 1, 15, 10, 30, 0)
        )

    def test_formato_html_sin_zona(self) -> None:
        self.assertEqual(
            parsear_fecha("15.01.2024 10:30:00"), datetime(2024, 1, 15, 10, 30, 0)
        )

    def test_solo_fecha(self) -> None:
        self.assertEqual(parsear_fecha("2024-01-15"), datetime(2024, 1, 15, 0, 0, 0))

    def test_marca_de_tiempo_unix(self) -> None:
        esperado = datetime.fromtimestamp(1705315800)
        self.assertEqual(parsear_fecha("1705315800"), esperado)

    def test_cadena_vacia_devuelve_none(self) -> None:
        self.assertIsNone(parsear_fecha(""))

    def test_formato_irreconocible_devuelve_none(self) -> None:
        self.assertIsNone(parsear_fecha("el martes pasado"))


class TestVentanaTemporal(unittest.TestCase):
    """Verifica la pertenencia de una fecha a una ventana."""

    def test_ventana_sin_limites_acepta_todo(self) -> None:
        ventana = VentanaTemporal()
        self.assertTrue(ventana.sin_limites)
        self.assertTrue(ventana.contiene(datetime(1999, 1, 1)))
        self.assertTrue(ventana.contiene(None))

    def test_respeta_el_limite_inferior(self) -> None:
        ventana = VentanaTemporal(desde=datetime(2024, 1, 1))
        self.assertFalse(ventana.contiene(datetime(2023, 12, 31)))
        self.assertTrue(ventana.contiene(datetime(2024, 1, 1)))
        self.assertTrue(ventana.contiene(datetime(2024, 6, 1)))

    def test_respeta_el_limite_superior(self) -> None:
        ventana = VentanaTemporal(hasta=datetime(2024, 1, 31, 23, 59, 59))
        self.assertTrue(ventana.contiene(datetime(2024, 1, 31, 10, 0)))
        self.assertFalse(ventana.contiene(datetime(2024, 2, 1)))

    def test_las_fechas_ilegibles_se_incluyen(self) -> None:
        """Perder coincidencias por un formato raro sería peor que incluirlas."""
        ventana = VentanaTemporal(desde=datetime(2024, 1, 1))
        self.assertTrue(ventana.contiene(None))


class TestConstruirVentanas(unittest.TestCase):
    """Verifica la construcción de las ventanas pedidas por el usuario."""

    def test_sin_argumentos_devuelve_una_ventana_sin_limites(self) -> None:
        ventanas = construir_ventanas(ahora=_AHORA)
        self.assertEqual(len(ventanas), 1)
        self.assertTrue(ventanas[0].sin_limites)
        self.assertEqual(ventanas[0].etiqueta, "")

    def test_una_sola_ventana_no_crea_subcarpeta(self) -> None:
        ventanas = construir_ventanas(dias=[30], ahora=_AHORA)
        self.assertEqual(len(ventanas), 1)
        self.assertEqual(ventanas[0].etiqueta, "")
        self.assertEqual(ventanas[0].desde, _AHORA - timedelta(days=30))

    def test_varias_ventanas_generan_subcarpetas_etiquetadas(self) -> None:
        ventanas = construir_ventanas(dias=[30, 60, 90, 160, 180], ahora=_AHORA)
        self.assertEqual(
            [v.etiqueta for v in ventanas],
            [
                "ultimos_30_dias",
                "ultimos_60_dias",
                "ultimos_90_dias",
                "ultimos_160_dias",
                "ultimos_180_dias",
            ],
        )

    def test_las_ventanas_salen_ordenadas_y_sin_repetidos(self) -> None:
        ventanas = construir_ventanas(dias=[90, 30, 60, 30], ahora=_AHORA)
        self.assertEqual(
            [v.etiqueta for v in ventanas],
            ["ultimos_30_dias", "ultimos_60_dias", "ultimos_90_dias"],
        )

    def test_las_ventanas_son_concentricas(self) -> None:
        ventanas = construir_ventanas(dias=[30, 180], ahora=_AHORA)
        self.assertGreater(ventanas[0].desde, ventanas[1].desde)

    def test_fechas_explicitas(self) -> None:
        desde = datetime(2024, 1, 1)
        hasta = datetime(2024, 3, 1)
        ventanas = construir_ventanas(desde=desde, hasta=hasta, ahora=_AHORA)
        self.assertEqual(ventanas[0].desde, desde)
        self.assertEqual(ventanas[0].hasta, hasta)

    def test_el_limite_explicito_recorta_la_ventana_en_dias(self) -> None:
        """Con --desde y --dias a la vez, gana el más restrictivo."""
        desde = datetime(2024, 6, 1)
        ventanas = construir_ventanas(dias=[180], desde=desde, ahora=_AHORA)
        self.assertEqual(ventanas[0].desde, desde)

    def test_los_dias_no_positivos_se_descartan(self) -> None:
        ventanas = construir_ventanas(dias=[0, -5], ahora=_AHORA)
        self.assertTrue(ventanas[0].sin_limites)


class TestVentanaEnvolvente(unittest.TestCase):
    """Verifica el cálculo de la ventana que engloba a todas las demás."""

    def test_devuelve_el_limite_inferior_mas_antiguo(self) -> None:
        ventanas = construir_ventanas(dias=[30, 60, 180], ahora=_AHORA)
        envolvente = ventana_envolvente(ventanas)
        self.assertEqual(envolvente.desde, _AHORA - timedelta(days=180))

    def test_una_ventana_sin_limites_hace_ilimitada_la_envolvente(self) -> None:
        envolvente = ventana_envolvente([VentanaTemporal(desde=_AHORA), VentanaTemporal()])
        self.assertTrue(envolvente.sin_limites)


class TestFiltrarMensajes(unittest.TestCase):
    """Verifica el descarte de mensajes antes de aplicar los regex."""

    def test_descarta_los_anteriores_al_limite(self) -> None:
        mensajes = [
            _mensaje("2024-06-10T10:00:00", "reciente"),
            _mensaje("2023-01-01T10:00:00", "antiguo"),
        ]
        ventana = VentanaTemporal(desde=_AHORA - timedelta(days=30))
        resultado = list(filtrar_mensajes(mensajes, ventana))
        self.assertEqual([m.id for m in resultado], ["reciente"])

    def test_sin_limites_no_descarta_nada(self) -> None:
        mensajes = [_mensaje("2024-06-10T10:00:00"), _mensaje("1999-01-01T10:00:00", "2")]
        self.assertEqual(len(list(filtrar_mensajes(mensajes, VentanaTemporal()))), 2)

    def test_es_perezoso(self) -> None:
        consumidos: list[str] = []

        def generador():
            for i in range(5):
                consumidos.append(str(i))
                yield _mensaje("2024-06-10T10:00:00", str(i))

        iterador = filtrar_mensajes(generador(), VentanaTemporal(desde=datetime(2024, 1, 1)))
        next(iterador)
        self.assertEqual(consumidos, ["0"])


class TestAsignadorVentanas(unittest.TestCase):
    """Verifica el reparto de cada coincidencia entre ventanas concéntricas."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)
        self.ventanas = construir_ventanas(dias=[30, 60, 90], ahora=_AHORA)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def test_un_mensaje_reciente_cae_en_todas_las_ventanas(self) -> None:
        asignador = AsignadorVentanas(self.ventanas)
        etiquetas = asignador.etiquetas_de(_mensaje("2024-06-10T10:00:00"))
        self.assertEqual(
            etiquetas, ("ultimos_30_dias", "ultimos_60_dias", "ultimos_90_dias")
        )

    def test_un_mensaje_intermedio_solo_cae_en_las_mas_amplias(self) -> None:
        asignador = AsignadorVentanas(self.ventanas)
        # 70 días atrás: fuera de 30 y 60, dentro de 90.
        etiquetas = asignador.etiquetas_de(_mensaje("2024-04-06T10:00:00"))
        self.assertEqual(etiquetas, ("ultimos_90_dias",))

    def test_un_mensaje_antiguo_no_cae_en_ninguna(self) -> None:
        asignador = AsignadorVentanas(self.ventanas)
        self.assertEqual(asignador.etiquetas_de(_mensaje("2020-01-01T10:00:00")), ())

    def test_memoriza_el_ultimo_mensaje_analizado(self) -> None:
        """Las coincidencias llegan agrupadas: no hay que reinterpretar la fecha."""
        asignador = AsignadorVentanas(self.ventanas)
        mensaje = _mensaje("2024-06-10T10:00:00")
        primera = asignador.etiquetas_de(mensaje)
        segunda = asignador.etiquetas_de(mensaje)
        self.assertIs(primera, segunda)

    def test_cuenta_las_fechas_ilegibles(self) -> None:
        asignador = AsignadorVentanas(self.ventanas)
        asignador.etiquetas_de(_mensaje("fecha rara"))
        self.assertEqual(asignador.fechas_ilegibles, 1)

    def test_una_fecha_vacia_no_cuenta_como_ilegible(self) -> None:
        asignador = AsignadorVentanas(self.ventanas)
        asignador.etiquetas_de(_mensaje(""))
        self.assertEqual(asignador.fechas_ilegibles, 0)


if __name__ == "__main__":
    unittest.main()
