"""Pruebas del motor de búsqueda."""

from __future__ import annotations

import logging
import re
import unittest

from core.models import Mensaje, PatronRegex
from core.search_engine import MotorBusqueda, buscar


def _patron(nombre: str, regex: str, linea: int = 1) -> PatronRegex:
    """Construye un patrón compilado para las pruebas."""
    return PatronRegex(nombre=nombre, regex=regex, compilado=re.compile(regex), numero_linea=linea)


def _mensaje(texto: str, identificador: str = "1", chat: str = "Chat") -> Mensaje:
    """Construye un mensaje normalizado para las pruebas."""
    return Mensaje(
        archivo="export.json",
        fecha="2024-01-01",
        usuario="Ana",
        texto=texto,
        id=identificador,
        chat=chat,
    )


class TestModosDeBusqueda(unittest.TestCase):
    """Verifica el comportamiento de search, findall y finditer."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def test_search_devuelve_solo_la_primera_coincidencia(self) -> None:
        resultados = list(buscar([_mensaje("a1 a2 a3")], [_patron("d", r"a\d")], "search"))
        self.assertEqual(len(resultados), 1)
        self.assertEqual(resultados[0].texto_encontrado, "a1")
        self.assertEqual(resultados[0].posicion, 0)

    def test_finditer_devuelve_todas_con_posicion(self) -> None:
        resultados = list(buscar([_mensaje("a1 a2 a3")], [_patron("d", r"a\d")], "finditer"))
        self.assertEqual([r.texto_encontrado for r in resultados], ["a1", "a2", "a3"])
        self.assertEqual([r.posicion for r in resultados], [0, 3, 6])

    def test_findall_devuelve_todas_sin_posicion(self) -> None:
        resultados = list(buscar([_mensaje("a1 a2")], [_patron("d", r"a\d")], "findall"))
        self.assertEqual([r.texto_encontrado for r in resultados], ["a1", "a2"])
        self.assertTrue(all(r.posicion == -1 for r in resultados))

    def test_findall_con_varios_grupos_no_vuelca_una_tupla(self) -> None:
        """El defecto clásico: str(tupla) escribía "('a', 'b')" en el resultado."""
        patron = _patron("fecha", r"(\d{2})/(\d{2})")
        resultados = list(buscar([_mensaje("el 24/12 fue")], [patron], "findall"))
        self.assertEqual(len(resultados), 1)
        self.assertEqual(resultados[0].texto_encontrado, "24 | 12")
        self.assertNotIn("(", resultados[0].texto_encontrado)

    def test_total_en_mensaje_refleja_el_numero_de_coincidencias(self) -> None:
        resultados = list(buscar([_mensaje("a1 a2 a3")], [_patron("d", r"a\d")], "finditer"))
        self.assertTrue(all(r.total_en_mensaje == 3 for r in resultados))

    def test_modo_no_soportado_lanza_en_lugar_de_callar(self) -> None:
        motor = MotorBusqueda([_patron("d", r"a")], "inexistente")  # type: ignore[arg-type]
        with self.assertRaises(ValueError):
            list(motor.buscar([_mensaje("a")]))

    def test_sin_patrones_no_produce_resultados(self) -> None:
        self.assertEqual(list(buscar([_mensaje("hola")], [], "search")), [])

    def test_varios_patrones_sobre_varios_mensajes(self) -> None:
        patrones = [_patron("letras", r"[a-z]+", 1), _patron("digitos", r"\d+", 2)]
        mensajes = [_mensaje("abc", "1"), _mensaje("123", "2")]
        resultados = list(buscar(mensajes, patrones, "search"))
        self.assertEqual(
            {(r.patron.nombre, r.mensaje.id) for r in resultados},
            {("letras", "1"), ("digitos", "2")},
        )


class TestDeduplicacion(unittest.TestCase):
    """Verifica el filtro de coincidencias duplicadas."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def test_descarta_el_mismo_mensaje_repetido(self) -> None:
        mensaje = _mensaje("hola", "7")
        motor = MotorBusqueda([_patron("s", "hola")], "search", evitar_duplicados=True)
        resultados = list(motor.buscar([mensaje, mensaje]))
        self.assertEqual(len(resultados), 1)
        self.assertEqual(motor.duplicados_descartados, 1)

    def test_sin_deduplicacion_se_emiten_todas(self) -> None:
        mensaje = _mensaje("hola", "7")
        motor = MotorBusqueda([_patron("s", "hola")], "search", evitar_duplicados=False)
        self.assertEqual(len(list(motor.buscar([mensaje, mensaje]))), 2)

    def test_mensajes_distintos_con_el_mismo_texto_no_son_duplicados(self) -> None:
        motor = MotorBusqueda([_patron("s", "hola")], "search", evitar_duplicados=True)
        resultados = list(motor.buscar([_mensaje("hola", "1"), _mensaje("hola", "2")]))
        self.assertEqual(len(resultados), 2)

    def test_mismo_id_en_chats_distintos_no_son_duplicados(self) -> None:
        motor = MotorBusqueda([_patron("s", "hola")], "search", evitar_duplicados=True)
        resultados = list(
            motor.buscar([_mensaje("hola", "1", "Chat A"), _mensaje("hola", "1", "Chat B")])
        )
        self.assertEqual(len(resultados), 2)


class TestEstadisticas(unittest.TestCase):
    """Verifica los contadores que alimentan el resumen final."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def test_cuenta_evaluaciones_y_coincidencias(self) -> None:
        motor = MotorBusqueda([_patron("a", "x"), _patron("b", "y", 2)], "search")
        list(motor.buscar([_mensaje("x"), _mensaje("y", "2"), _mensaje("z", "3")]))
        # 3 mensajes x 2 patrones = 6 evaluaciones.
        self.assertEqual(motor.evaluaciones, 6)
        self.assertEqual(motor.coincidencias_emitidas, 2)

    def test_estadisticas_expone_el_resumen(self) -> None:
        motor = MotorBusqueda([_patron("a", "x")], "search")
        list(motor.buscar([_mensaje("x")]))
        estadisticas = motor.estadisticas()
        self.assertEqual(estadisticas["patrones"], 1)
        self.assertEqual(estadisticas["coincidencias"], 1)
        self.assertIsNotNone(estadisticas["patron_mas_lento"])

    def test_es_perezoso_no_consume_el_iterador_de_golpe(self) -> None:
        """El motor debe emitir resultados sin agotar antes toda la entrada."""
        consumidos: list[str] = []

        def generador():
            for i in range(5):
                consumidos.append(str(i))
                yield _mensaje("x", str(i))

        motor = MotorBusqueda([_patron("a", "x")], "search", evitar_duplicados=False)
        iterador = motor.buscar(generador())
        next(iterador)
        self.assertEqual(consumidos, ["0"])


if __name__ == "__main__":
    unittest.main()
