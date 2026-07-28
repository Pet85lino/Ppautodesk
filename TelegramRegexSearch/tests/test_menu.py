"""Pruebas del menú interactivo."""

from __future__ import annotations

import argparse
import io
import logging
import unittest
from unittest import mock

from io_utils.menu import (
    OpcionMenu,
    bucle_menu,
    confirmar,
    elegir_opcion,
    pedir_entero,
    pedir_opcion_de_lista,
    pedir_texto,
)
from main import _debe_mostrar_menu


def _opciones(registro: list[str]) -> list[OpcionMenu]:
    """Construye un menú de prueba que anota las acciones ejecutadas."""
    return [
        OpcionMenu("1", "Primera", lambda: (registro.append("1"), True)[1]),
        OpcionMenu("2", "Segunda", lambda: (registro.append("2"), True)[1]),
        OpcionMenu("3", "Salir", lambda: False),
    ]


class TestElegirOpcion(unittest.TestCase):
    """Verifica la interpretación de lo que teclea el usuario."""

    def setUp(self) -> None:
        self.opciones = _opciones([])

    def test_encuentra_la_opcion_por_su_numero(self) -> None:
        elegida = elegir_opcion("2", self.opciones)
        self.assertIsNotNone(elegida)
        assert elegida is not None
        self.assertEqual(elegida.titulo, "Segunda")

    def test_tolera_espacios_alrededor(self) -> None:
        """En un teclado táctil es fácil colar un espacio de más."""
        for entrada in (" 1", "1 ", "  1  "):
            self.assertIsNotNone(elegir_opcion(entrada, self.opciones))

    def test_una_opcion_inexistente_devuelve_none(self) -> None:
        for entrada in ("9", "", "hola", "-1"):
            self.assertIsNone(elegir_opcion(entrada, self.opciones))


class TestEntradasDelUsuario(unittest.TestCase):
    """Verifica los ayudantes que piden datos por consola."""

    def setUp(self) -> None:
        # Estos ayudantes imprimen las opciones; se captura la salida para
        # que el informe de las pruebas quede limpio.
        self._parche = mock.patch("sys.stdout", io.StringIO())
        self._parche.start()

    def tearDown(self) -> None:
        self._parche.stop()

    def test_texto_devuelve_lo_introducido(self) -> None:
        with mock.patch("builtins.input", return_value="  hola  "):
            self.assertEqual(pedir_texto("Mensaje"), "hola")

    def test_texto_vacio_usa_el_valor_por_defecto(self) -> None:
        with mock.patch("builtins.input", return_value=""):
            self.assertEqual(pedir_texto("Mensaje", "defecto"), "defecto")

    def test_texto_sobrevive_a_una_interrupcion(self) -> None:
        with mock.patch("builtins.input", side_effect=EOFError):
            self.assertEqual(pedir_texto("Mensaje", "defecto"), "defecto")

    def test_entero_valido(self) -> None:
        with mock.patch("builtins.input", return_value="500"):
            self.assertEqual(pedir_entero("Límite"), 500)

    def test_entero_no_numerico_usa_el_valor_por_defecto(self) -> None:
        with mock.patch("builtins.input", return_value="muchos"):
            self.assertIsNone(pedir_entero("Límite"))

    def test_entero_no_positivo_se_rechaza(self) -> None:
        with mock.patch("builtins.input", return_value="0"):
            self.assertEqual(pedir_entero("Límite", 10), 10)

    def test_entero_vacio_usa_el_valor_por_defecto(self) -> None:
        with mock.patch("builtins.input", return_value=""):
            self.assertEqual(pedir_entero("Límite", 99), 99)

    def test_lista_admite_elegir_por_numero(self) -> None:
        with mock.patch("builtins.input", return_value="2"):
            self.assertEqual(
                pedir_opcion_de_lista("Tipo", ["todos", "grupos", "canales"], "todos"),
                "grupos",
            )

    def test_lista_admite_elegir_por_nombre(self) -> None:
        with mock.patch("builtins.input", return_value="canales"):
            self.assertEqual(
                pedir_opcion_de_lista("Tipo", ["todos", "grupos", "canales"], "todos"),
                "canales",
            )

    def test_lista_fuera_de_rango_usa_el_valor_por_defecto(self) -> None:
        with mock.patch("builtins.input", return_value="99"):
            self.assertEqual(
                pedir_opcion_de_lista("Tipo", ["todos", "grupos"], "todos"), "todos"
            )

    def test_lista_con_texto_desconocido_usa_el_valor_por_defecto(self) -> None:
        with mock.patch("builtins.input", return_value="inventado"):
            self.assertEqual(
                pedir_opcion_de_lista("Tipo", ["todos", "grupos"], "grupos"), "grupos"
            )

    def test_confirmar_interpreta_las_respuestas(self) -> None:
        for respuesta, esperado in (("s", True), ("S", True), ("si", True), ("n", False)):
            with mock.patch("builtins.input", return_value=respuesta):
                self.assertIs(confirmar("¿Seguro?"), esperado)

    def test_confirmar_vacio_usa_el_valor_por_defecto(self) -> None:
        with mock.patch("builtins.input", return_value=""):
            self.assertIs(confirmar("¿Seguro?", por_defecto=True), True)
            self.assertIs(confirmar("¿Seguro?", por_defecto=False), False)


class TestBucleMenu(unittest.TestCase):
    """Verifica el bucle principal del menú."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)
        self.salida = io.StringIO()

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def _ejecutar(self, entradas: list[str], opciones: list[OpcionMenu]) -> None:
        with mock.patch("builtins.input", side_effect=entradas), mock.patch(
            "sys.stdout", self.salida
        ):
            bucle_menu("Prueba", opciones)

    def test_ejecuta_la_accion_elegida_y_sale(self) -> None:
        registro: list[str] = []
        self._ejecutar(["1", "3"], _opciones(registro))
        self.assertEqual(registro, ["1"])

    def test_repite_hasta_que_se_elige_salir(self) -> None:
        registro: list[str] = []
        self._ejecutar(["1", "2", "1", "3"], _opciones(registro))
        self.assertEqual(registro, ["1", "2", "1"])

    def test_una_opcion_invalida_no_cierra_el_menu(self) -> None:
        registro: list[str] = []
        self._ejecutar(["9", "1", "3"], _opciones(registro))
        self.assertEqual(registro, ["1"])
        self.assertIn("no existe", self.salida.getvalue())

    def test_un_error_en_una_accion_no_cierra_el_menu(self) -> None:
        """Un fallo puntual debe devolver al menú, no terminar el programa."""
        registro: list[str] = []

        def _falla() -> bool:
            raise RuntimeError("algo se rompió")

        opciones = [
            OpcionMenu("1", "Falla", _falla),
            OpcionMenu("2", "Bien", lambda: (registro.append("ok"), True)[1]),
            OpcionMenu("3", "Salir", lambda: False),
        ]
        self._ejecutar(["1", "2", "3"], opciones)

        self.assertEqual(registro, ["ok"])
        self.assertIn("algo se rompió", self.salida.getvalue())

    def test_una_cancelacion_devuelve_al_menu(self) -> None:
        registro: list[str] = []

        def _cancela() -> bool:
            raise KeyboardInterrupt

        opciones = [
            OpcionMenu("1", "Cancela", _cancela),
            OpcionMenu("2", "Bien", lambda: (registro.append("ok"), True)[1]),
            OpcionMenu("3", "Salir", lambda: False),
        ]
        self._ejecutar(["1", "2", "3"], opciones)

        self.assertEqual(registro, ["ok"])
        self.assertIn("cancelada", self.salida.getvalue())

    def test_el_fin_de_entrada_cierra_el_menu(self) -> None:
        self._ejecutar([EOFError()], _opciones([]))  # type: ignore[list-item]
        self.assertIn("Hasta luego", self.salida.getvalue())

    def test_muestra_todas_las_opciones(self) -> None:
        self._ejecutar(["3"], _opciones([]))
        texto = self.salida.getvalue()
        for titulo in ("Primera", "Segunda", "Salir"):
            self.assertIn(titulo, texto)


class TestDebeMostrarMenu(unittest.TestCase):
    """Verifica cuándo procede abrir el menú.

    Es la decisión que separa el uso desde Pydroid 3 —donde no se pueden
    pasar argumentos— del uso automatizado, que no debe cambiar.
    """

    def _argumentos(self, sin_menu: bool = False) -> argparse.Namespace:
        return argparse.Namespace(sin_menu=sin_menu)

    def test_sin_argumentos_y_con_consola_interactiva_se_abre(self) -> None:
        with mock.patch("sys.stdin") as entrada:
            entrada.isatty.return_value = True
            self.assertTrue(_debe_mostrar_menu([], self._argumentos()))

    def test_con_argumentos_no_se_abre(self) -> None:
        with mock.patch("sys.stdin") as entrada:
            entrada.isatty.return_value = True
            self.assertFalse(_debe_mostrar_menu(["--modo", "search"], self._argumentos()))

    def test_sin_consola_interactiva_no_se_abre(self) -> None:
        """Con la entrada redirigida, el menú se quedaría esperando siempre."""
        with mock.patch("sys.stdin") as entrada:
            entrada.isatty.return_value = False
            self.assertFalse(_debe_mostrar_menu([], self._argumentos()))

    def test_la_opcion_sin_menu_manda(self) -> None:
        with mock.patch("sys.stdin") as entrada:
            entrada.isatty.return_value = True
            self.assertFalse(_debe_mostrar_menu([], self._argumentos(sin_menu=True)))

    def test_una_entrada_estandar_ausente_no_rompe(self) -> None:
        with mock.patch("sys.stdin", None):
            self.assertFalse(_debe_mostrar_menu([], self._argumentos()))

    def test_una_entrada_estandar_cerrada_no_rompe(self) -> None:
        with mock.patch("sys.stdin") as entrada:
            entrada.isatty.side_effect = ValueError("closed file")
            self.assertFalse(_debe_mostrar_menu([], self._argumentos()))


if __name__ == "__main__":
    unittest.main()
