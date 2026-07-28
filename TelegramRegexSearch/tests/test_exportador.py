"""Pruebas del exportador de resultados."""

from __future__ import annotations

import logging
import re
import tempfile
import unittest
from pathlib import Path

from core.models import Coincidencia, Mensaje, PatronRegex
from io_utils.exportador import ExportadorResultados


def _coincidencia(nombre_patron: str, texto: str = "hola", posicion: int = 0) -> Coincidencia:
    """Construye una coincidencia lista para exportar."""
    patron = PatronRegex(
        nombre=nombre_patron, regex=texto, compilado=re.compile(re.escape(texto)), numero_linea=1
    )
    mensaje = Mensaje(
        archivo="export.json",
        fecha="2024-01-01",
        usuario="Ana",
        texto=f"mensaje completo con {texto}",
        id="42",
        chat="Mi Chat",
    )
    return Coincidencia(patron, mensaje, texto, posicion, total_en_mensaje=1)


class TestExportadorResultados(unittest.TestCase):
    """Verifica la escritura de los archivos de resultados."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name) / "resultados"
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def test_crea_la_carpeta_y_un_archivo_por_patron(self) -> None:
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("correos"))
            exportador.exportar(_coincidencia("urls"))

        self.assertTrue(self.base.is_dir())
        archivos = sorted(p.name for p in self.base.glob("*.txt"))
        self.assertEqual(archivos, ["correos.txt", "urls.txt"])

    def test_el_bloque_contiene_todos_los_campos(self) -> None:
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("correos", "a@b.com", 21))

        contenido = (self.base / "correos.txt").read_text(encoding="utf-8")
        for esperado in (
            "=" * 50,
            "Archivo: export.json",
            "Chat: Mi Chat",
            "Usuario: Ana",
            "Fecha: 2024-01-01",
            "ID mensaje: 42",
            "Regex: a@b.com",
            "Coincidencia: a@b.com",
            "Posicion: 21",
            "Mensaje completo:",
            "mensaje completo con a@b.com",
        ):
            self.assertIn(esperado, contenido)

    def test_posicion_no_disponible_se_muestra_como_nd(self) -> None:
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("modo_findall", "x", -1))
        self.assertIn("Posicion: n/d", (self.base / "modo_findall.txt").read_text(encoding="utf-8"))

    def test_cada_ejecucion_reemplaza_los_resultados_anteriores(self) -> None:
        """Defecto detectado en la auditoría: el modo 'a' acumulaba ejecuciones."""
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("correos", "primera"))

        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("correos", "segunda"))

        contenido = (self.base / "correos.txt").read_text(encoding="utf-8")
        self.assertIn("segunda", contenido)
        self.assertNotIn("primera", contenido)

    def test_el_limite_de_archivos_abiertos_no_pierde_contenido(self) -> None:
        """Con la caché LRU al mínimo, reabrir un archivo debe conservar lo escrito."""
        with ExportadorResultados(self.base, max_archivos_abiertos=1) as exportador:
            exportador.exportar(_coincidencia("uno", "primera_de_uno"))
            exportador.exportar(_coincidencia("dos", "primera_de_dos"))
            exportador.exportar(_coincidencia("uno", "segunda_de_uno"))

        contenido = (self.base / "uno.txt").read_text(encoding="utf-8")
        self.assertIn("primera_de_uno", contenido)
        self.assertIn("segunda_de_uno", contenido)
        self.assertIn("primera_de_dos", (self.base / "dos.txt").read_text(encoding="utf-8"))

    def test_sanea_los_nombres_de_archivo_invalidos(self) -> None:
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia('correo:raro/con*chars?'))

        archivos = list(self.base.glob("*.txt"))
        self.assertEqual(len(archivos), 1)
        self.assertNotIn(":", archivos[0].name)
        self.assertNotIn("/", archivos[0].name)

    def test_nombres_distintos_que_colisionan_no_mezclan_resultados(self) -> None:
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("a/b", "de_barra"))
            exportador.exportar(_coincidencia("a:b", "de_dospuntos"))

        archivos = sorted(p.name for p in self.base.glob("*.txt"))
        self.assertEqual(len(archivos), 2)
        contenidos = [(self.base / nombre).read_text(encoding="utf-8") for nombre in archivos]
        self.assertTrue(any("de_barra" in c and "de_dospuntos" not in c for c in contenidos))

    def test_no_escapa_de_la_carpeta_de_resultados(self) -> None:
        """Un nombre de patrón con '..' no debe escribir fuera del destino."""
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("../../fuera"))

        archivos = list(self.base.glob("*.txt"))
        self.assertEqual(len(archivos), 1)
        self.assertEqual(archivos[0].parent, self.base)

    def test_contadores_publicos(self) -> None:
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("uno"))
            exportador.exportar(_coincidencia("uno", "otra"))
            exportador.exportar(_coincidencia("dos"))
            self.assertEqual(exportador.coincidencias_escritas, 3)
            self.assertEqual(exportador.archivos_generados, 2)

    def test_no_crea_archivos_para_patrones_sin_coincidencias(self) -> None:
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("solo_este"))
        self.assertEqual([p.name for p in self.base.glob("*.txt")], ["solo_este.txt"])

    def test_la_cabecera_identifica_el_patron(self) -> None:
        with ExportadorResultados(self.base) as exportador:
            exportador.exportar(_coincidencia("correos", "a@b.com"))
        contenido = (self.base / "correos.txt").read_text(encoding="utf-8")
        self.assertTrue(contenido.startswith("# Patron: correos"))


if __name__ == "__main__":
    unittest.main()
