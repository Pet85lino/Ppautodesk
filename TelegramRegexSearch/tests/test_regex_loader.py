"""Pruebas del cargador de patrones regex."""

from __future__ import annotations

import logging
import tempfile
import unittest
from pathlib import Path

from core.regex_loader import cargar_patrones


class TestCargarPatrones(unittest.TestCase):
    """Verifica el parseo, la validación y la compilación de patrones.txt."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        # Silencia los logs esperados de patrones inválidos.
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def _escribir(self, contenido: str) -> Path:
        ruta = self.base / "patrones.txt"
        ruta.write_text(contenido, encoding="utf-8")
        return ruta

    def test_ignora_comentarios_y_lineas_vacias(self) -> None:
        ruta = self._escribir("# comentario\n\n   \nhola\n#otro\n")
        patrones = cargar_patrones(ruta)
        self.assertEqual(len(patrones), 1)
        self.assertEqual(patrones[0].regex, "hola")

    def test_asigna_nombre_automatico_con_numero_de_linea(self) -> None:
        ruta = self._escribir("# c\n\nhola\n")
        patrones = cargar_patrones(ruta)
        self.assertEqual(patrones[0].nombre, "patron_3")
        self.assertEqual(patrones[0].numero_linea, 3)

    def test_respeta_el_nombre_explicito(self) -> None:
        ruta = self._escribir("saludos :: hola|adios\n")
        patrones = cargar_patrones(ruta)
        self.assertEqual(patrones[0].nombre, "saludos")
        self.assertEqual(patrones[0].regex, "hola|adios")

    def test_descarta_regex_invalido_sin_abortar_el_resto(self) -> None:
        ruta = self._escribir("bueno :: \\d+\n[sin_cerrar\notro :: [a-z]+\n")
        patrones = cargar_patrones(ruta)
        self.assertEqual([p.nombre for p in patrones], ["bueno", "otro"])

    def test_no_parte_un_regex_que_contiene_dos_puntos_dobles(self) -> None:
        # "a{2}::b" empieza por metacaracteres: la parte izquierda no es un
        # nombre, así que la línea entera debe tratarse como el regex.
        ruta = self._escribir("a{2}::b\n")
        patrones = cargar_patrones(ruta)
        self.assertEqual(patrones[0].regex, "a{2}::b")
        self.assertEqual(patrones[0].nombre, "patron_1")

    def test_renombra_patrones_con_nombre_duplicado(self) -> None:
        ruta = self._escribir("dup :: uno\ndup :: dos\n")
        patrones = cargar_patrones(ruta)
        self.assertEqual([p.nombre for p in patrones], ["dup", "dup_2"])

    def test_descarta_regex_vacio_tras_el_separador(self) -> None:
        ruta = self._escribir("vacio ::   \nbueno :: x\n")
        patrones = cargar_patrones(ruta)
        self.assertEqual([p.nombre for p in patrones], ["bueno"])

    def test_ignorar_mayusculas_aplica_a_todos_los_patrones(self) -> None:
        ruta = self._escribir("saludo :: hola\n")
        sensible = cargar_patrones(ruta, ignorar_mayusculas=False)
        insensible = cargar_patrones(ruta, ignorar_mayusculas=True)
        self.assertIsNone(sensible[0].compilado.search("HOLA"))
        self.assertIsNotNone(insensible[0].compilado.search("HOLA"))

    def test_prefijo_inline_tiene_efecto(self) -> None:
        ruta = self._escribir("saludo :: (?i)hola\n")
        patrones = cargar_patrones(ruta)
        self.assertIsNotNone(patrones[0].compilado.search("HoLa"))

    def test_archivo_inexistente_devuelve_lista_vacia(self) -> None:
        self.assertEqual(cargar_patrones(self.base / "no_existe.txt"), [])


if __name__ == "__main__":
    unittest.main()
