"""Pruebas de las utilidades transversales: filesystem, progreso y dependencias."""

from __future__ import annotations

import io
import logging
import tempfile
import unittest
from pathlib import Path

from io_utils.progress import RastreadorProgreso
from utils.credenciales import cargar_credenciales
from utils.dependencias import (
    Requisito,
    asegurar_paquetes,
    modulo_disponible,
    verificar_entorno,
    verificar_modulos_estandar,
    verificar_version_python,
)
from utils.filesystem import crear_carpetas, resolver_ruta, sanear_nombre_archivo


class TestFilesystem(unittest.TestCase):
    """Verifica la creación de carpetas y el saneado de nombres."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def test_crea_carpetas_anidadas_y_es_idempotente(self) -> None:
        carpetas = [self.base / "a" / "b", self.base / "c"]
        crear_carpetas(carpetas)
        crear_carpetas(carpetas)
        self.assertTrue(all(carpeta.is_dir() for carpeta in carpetas))

    def test_sustituye_los_caracteres_invalidos(self) -> None:
        self.assertEqual(sanear_nombre_archivo('a<b>c:d"e/f\\g|h?i*j'), "a_b_c_d_e_f_g_h_i_j")

    def test_neutraliza_los_saltos_de_directorio(self) -> None:
        for peligroso in ("../../etc/passwd", "..", "./..", "/etc/passwd"):
            saneado = sanear_nombre_archivo(peligroso)
            self.assertNotIn("/", saneado)
            self.assertNotIn("\\", saneado)
            self.assertNotEqual(saneado, "..")

    def test_nombre_vacio_tiene_valor_de_reserva(self) -> None:
        self.assertEqual(sanear_nombre_archivo("   "), "patron_sin_nombre")
        self.assertEqual(sanear_nombre_archivo("..."), "patron_sin_nombre")

    def test_esquiva_los_nombres_reservados_de_windows(self) -> None:
        for reservado in ("CON", "con", "NUL", "COM1", "LPT9"):
            self.assertTrue(sanear_nombre_archivo(reservado).startswith("_"))

    def test_recorta_los_nombres_demasiado_largos(self) -> None:
        self.assertEqual(len(sanear_nombre_archivo("x" * 500)), 120)

    def test_resolver_ruta_relativa_y_absoluta(self) -> None:
        self.assertEqual(resolver_ruta(self.base, "datos"), self.base / "datos")
        absoluta = (self.base / "otro").resolve()
        self.assertEqual(resolver_ruta(self.base, str(absoluta)), absoluta)


class TestRastreadorProgreso(unittest.TestCase):
    """Verifica los contadores y el pintado de la barra de progreso."""

    def test_se_desactiva_cuando_la_salida_no_es_terminal(self) -> None:
        salida = io.StringIO()  # StringIO no es un TTY.
        progreso = RastreadorProgreso(1, salida=salida)
        progreso.registrar_mensaje()
        progreso.finalizar()
        self.assertEqual(salida.getvalue(), "")

    def test_los_contadores_se_actualizan(self) -> None:
        progreso = RastreadorProgreso(1, salida=io.StringIO(), forzar_activo=False)
        progreso.registrar_archivo()
        progreso.registrar_mensaje()
        progreso.registrar_mensaje()
        progreso.registrar_evaluaciones_regex(6)
        progreso.registrar_coincidencia()
        self.assertEqual(progreso.archivos_procesados, 1)
        self.assertEqual(progreso.mensajes_procesados, 2)
        self.assertEqual(progreso.regex_evaluados, 6)
        self.assertEqual(progreso.coincidencias_encontradas, 1)

    def test_el_resumen_incluye_todas_las_metricas(self) -> None:
        progreso = RastreadorProgreso(1, salida=io.StringIO(), forzar_activo=False)
        resumen = progreso.resumen()
        for etiqueta in ("Archivos:", "Mensajes:", "Regex:", "Coincidencias:", "Tiempo:", "Msj/s:"):
            self.assertIn(etiqueta, resumen)

    def test_pinta_cuando_esta_forzado_a_activo(self) -> None:
        salida = io.StringIO()
        progreso = RastreadorProgreso(1, salida=salida, forzar_activo=True)
        progreso.mostrar(forzar=True)
        self.assertIn("Mensajes:", salida.getvalue())
        self.assertIn("\r", salida.getvalue())

    def test_la_linea_nunca_excede_el_ancho_de_la_terminal(self) -> None:
        salida = io.StringIO()
        progreso = RastreadorProgreso(1, salida=salida, forzar_activo=True)
        progreso.mensajes_procesados = 10**12
        progreso.mostrar(forzar=True)
        lineas = [parte for parte in salida.getvalue().split("\r") if parte]
        ancho = progreso._ancho_terminal()  # noqa: SLF001 - se comprueba el recorte
        self.assertTrue(all(len(linea) < ancho for linea in lineas))

    def test_una_salida_cerrada_no_propaga_el_error(self) -> None:
        salida = io.StringIO()
        progreso = RastreadorProgreso(1, salida=salida, forzar_activo=True)
        salida.close()
        progreso.mostrar(forzar=True)  # No debe lanzar.
        progreso.finalizar()

    def test_limpiar_linea_borra_lo_pintado(self) -> None:
        salida = io.StringIO()
        progreso = RastreadorProgreso(1, salida=salida, forzar_activo=True)
        progreso.mostrar(forzar=True)
        progreso.limpiar_linea()
        self.assertTrue(salida.getvalue().endswith("\r"))

    def test_gestor_de_contexto_cierra_la_linea(self) -> None:
        salida = io.StringIO()
        with RastreadorProgreso(1, salida=salida, forzar_activo=True) as progreso:
            progreso.registrar_mensaje()
        self.assertTrue(salida.getvalue().endswith("\n"))


class TestDependencias(unittest.TestCase):
    """Verifica el chequeo de requisitos del entorno."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def test_la_version_de_python_en_uso_es_suficiente(self) -> None:
        self.assertTrue(verificar_version_python())

    def test_no_falta_ningun_modulo_estandar(self) -> None:
        self.assertEqual(verificar_modulos_estandar(), [])

    def test_el_entorno_completo_se_valida(self) -> None:
        self.assertTrue(verificar_entorno())

    def test_detecta_modulos_disponibles_e_inexistentes(self) -> None:
        self.assertTrue(modulo_disponible("json"))
        self.assertFalse(modulo_disponible("modulo_que_no_existe_12345"))

    def test_sin_instalacion_automatica_no_toca_el_entorno(self) -> None:
        requisito = Requisito("modulo_que_no_existe_12345", "paquete-inexistente", "prueba")
        faltantes = asegurar_paquetes([requisito], instalar_automaticamente=False)
        self.assertEqual(faltantes, [requisito])

    def test_un_paquete_ya_presente_no_se_reporta_como_faltante(self) -> None:
        self.assertEqual(asegurar_paquetes([Requisito("json", "json", "stdlib")]), [])

    def test_rechaza_nombres_de_paquete_peligrosos(self) -> None:
        """Un nombre que empiece por '-' se interpretaría como opción de pip."""
        peligroso = Requisito("modulo_que_no_existe_12345", "--upgrade", "inyección")
        faltantes = asegurar_paquetes([peligroso], instalar_automaticamente=True)
        self.assertEqual(faltantes, [peligroso])


class TestCredenciales(unittest.TestCase):
    """Verifica la carga y validación de credenciales."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def test_sin_credenciales_devuelve_none(self) -> None:
        self.assertIsNone(cargar_credenciales(self.base))

    def test_carga_desde_archivo_valido(self) -> None:
        (self.base / "credenciales.json").write_text(
            '{"api_id": 12345, "api_hash": "0123456789abcdef0123456789abcdef"}',
            encoding="utf-8",
        )
        credenciales = cargar_credenciales(self.base)
        self.assertIsNotNone(credenciales)
        assert credenciales is not None
        self.assertEqual(credenciales.api_id, 12345)

    def test_el_hash_nunca_aparece_entero_al_enmascararlo(self) -> None:
        (self.base / "credenciales.json").write_text(
            '{"api_id": 1, "api_hash": "0123456789abcdef0123456789abcdef"}', encoding="utf-8"
        )
        credenciales = cargar_credenciales(self.base)
        assert credenciales is not None
        self.assertNotIn(credenciales.api_hash, credenciales.enmascarado())
        self.assertIn("...", credenciales.enmascarado())

    def test_hash_con_formato_invalido_se_rechaza(self) -> None:
        (self.base / "credenciales.json").write_text(
            '{"api_id": 1, "api_hash": "demasiado_corto"}', encoding="utf-8"
        )
        self.assertIsNone(cargar_credenciales(self.base))

    def test_api_id_no_numerico_se_rechaza(self) -> None:
        (self.base / "credenciales.json").write_text(
            '{"api_id": "abc", "api_hash": "0123456789abcdef0123456789abcdef"}', encoding="utf-8"
        )
        self.assertIsNone(cargar_credenciales(self.base))

    def test_archivo_corrupto_no_lanza(self) -> None:
        (self.base / "credenciales.json").write_text("{ roto", encoding="utf-8")
        self.assertIsNone(cargar_credenciales(self.base))


if __name__ == "__main__":
    unittest.main()
