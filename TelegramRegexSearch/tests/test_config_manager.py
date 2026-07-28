"""Pruebas del gestor de configuración."""

from __future__ import annotations

import json
import logging
import tempfile
import unittest
from pathlib import Path

from config.config_manager import CONFIG_POR_DEFECTO, cargar_configuracion


class TestCargarConfiguracion(unittest.TestCase):
    """Verifica la creación, la validación y el saneado de config.json."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.ruta = Path(self._directorio.name) / "config.json"
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def _escribir(self, datos: object) -> None:
        self.ruta.write_text(json.dumps(datos), encoding="utf-8")

    def test_crea_el_archivo_con_valores_por_defecto(self) -> None:
        config = cargar_configuracion(self.ruta)
        self.assertTrue(self.ruta.is_file())
        self.assertEqual(config.como_diccionario(), CONFIG_POR_DEFECTO)

    def test_conserva_los_valores_personalizados(self) -> None:
        self._escribir({"modo_busqueda": "finditer", "carpeta_datos": "mis_datos"})
        config = cargar_configuracion(self.ruta)
        self.assertEqual(config.modo_busqueda, "finditer")
        self.assertEqual(config.carpeta_datos, "mis_datos")

    def test_completa_las_claves_ausentes(self) -> None:
        self._escribir({"modo_busqueda": "findall"})
        config = cargar_configuracion(self.ruta)
        self.assertEqual(config.carpeta_resultados, "resultados")
        # El archivo se reescribe con la configuración completa.
        guardado = json.loads(self.ruta.read_text(encoding="utf-8"))
        self.assertEqual(set(guardado), set(CONFIG_POR_DEFECTO))

    def test_modo_de_busqueda_invalido_se_sustituye(self) -> None:
        """Defecto de la auditoría: un modo inválido daba cero resultados en silencio."""
        self._escribir({"modo_busqueda": "buscar"})
        self.assertEqual(cargar_configuracion(self.ruta).modo_busqueda, "search")

    def test_entero_invalido_se_sustituye(self) -> None:
        self._escribir({"actualizar_progreso_cada_n_mensajes": "cincuenta"})
        config = cargar_configuracion(self.ruta)
        self.assertEqual(config.actualizar_progreso_cada_n_mensajes, 200)

    def test_entero_fuera_de_rango_se_sustituye(self) -> None:
        self._escribir({"actualizar_progreso_cada_n_mensajes": 0})
        self.assertEqual(cargar_configuracion(self.ruta).actualizar_progreso_cada_n_mensajes, 200)

    def test_booleano_no_estricto_se_sustituye(self) -> None:
        self._escribir({"evitar_duplicados": 1})
        self.assertIs(cargar_configuracion(self.ruta).evitar_duplicados, True)
        self._escribir({"evitar_duplicados": "no"})
        self.assertIs(cargar_configuracion(self.ruta).evitar_duplicados, True)

    def test_codificacion_inexistente_se_sustituye(self) -> None:
        self._escribir({"codificacion_salida": "utf-999"})
        self.assertEqual(cargar_configuracion(self.ruta).codificacion_salida, "utf-8")

    def test_cadena_vacia_se_sustituye(self) -> None:
        self._escribir({"carpeta_datos": "   "})
        self.assertEqual(cargar_configuracion(self.ruta).carpeta_datos, "datos")

    def test_normaliza_las_extensiones(self) -> None:
        self._escribir({"extensiones_soportadas": ["JSON", ".HTML"]})
        self.assertEqual(cargar_configuracion(self.ruta).extensiones_soportadas, [".json", ".html"])

    def test_descarta_extensiones_no_soportadas(self) -> None:
        self._escribir({"extensiones_soportadas": [".json", ".docx"]})
        self.assertEqual(cargar_configuracion(self.ruta).extensiones_soportadas, [".json"])

    def test_lista_de_extensiones_vacia_vuelve_al_valor_por_defecto(self) -> None:
        self._escribir({"extensiones_soportadas": []})
        self.assertEqual(cargar_configuracion(self.ruta).extensiones_soportadas, [".json"])

    def test_json_corrupto_no_lanza(self) -> None:
        self.ruta.write_text("{ esto no es json", encoding="utf-8")
        self.assertEqual(cargar_configuracion(self.ruta).modo_busqueda, "search")

    def test_json_que_no_es_objeto_no_lanza(self) -> None:
        self._escribir([1, 2, 3])
        self.assertEqual(cargar_configuracion(self.ruta).modo_busqueda, "search")

    def test_no_reescribe_si_la_configuracion_ya_es_valida(self) -> None:
        cargar_configuracion(self.ruta)
        marca = self.ruta.stat().st_mtime_ns
        cargar_configuracion(self.ruta)
        self.assertEqual(self.ruta.stat().st_mtime_ns, marca)


if __name__ == "__main__":
    unittest.main()
