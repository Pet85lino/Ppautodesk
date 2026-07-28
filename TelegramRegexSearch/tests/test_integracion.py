"""Pruebas de integración: ejecutan el pipeline completo de extremo a extremo.

Cada prueba monta un proyecto completo en una carpeta temporal (datos,
patrones, config) e invoca ``main()`` igual que lo haría el usuario desde la
consola, comprobando el código de salida, los archivos generados y su
contenido.
"""

from __future__ import annotations

import contextlib
import io
import json
import tempfile
import unittest
from datetime import datetime, timedelta
from pathlib import Path

import main as aplicacion
from main import EXITO, SIN_TRABAJO

_EXPORT_UN_CHAT = {
    "name": "Chat con Ana",
    "type": "personal_chat",
    "id": 111,
    "messages": [
        {
            "id": 1,
            "type": "message",
            "date": "2024-01-01T10:00:00",
            "from": "Ana",
            "text": "mi correo es ana@ejemplo.com, escribeme",
        },
        {
            "id": 2,
            "type": "service",
            "date": "2024-01-01T10:01:00",
            "actor": "Ana",
            "action": "pin_message",
        },
        {
            "id": 3,
            "type": "message",
            "date": "2024-01-01T10:02:00",
            "from": "Peter",
            "text": ["mira ", {"type": "link", "text": "https://ejemplo.com/x"}, " gracias"],
        },
        {
            "id": 4,
            "type": "message",
            "date": "2024-01-01T10:03:00",
            "from": "Peter",
            "text": "sin nada interesante",
        },
    ],
}

_EXPORT_CUENTA_COMPLETA = {
    "about": "Export completo",
    "personal_information": {"first_name": "Peter"},
    "chats": {
        "list": [
            {
                "name": "Grupo Trabajo",
                "type": "private_group",
                "messages": [
                    {
                        "id": 10,
                        "type": "message",
                        "date": "2024-02-01T09:00:00",
                        "from": "Beto",
                        "text": "soporte@empresa.com para incidencias",
                    }
                ],
            },
            {
                "name": "Canal Noticias",
                "type": "public_channel",
                "messages": [
                    {
                        "id": 20,
                        "type": "message",
                        "date": "2024-02-02T09:00:00",
                        "from": None,
                        "from_id": "channel999",
                        "text": "visita https://noticias.example",
                    }
                ],
            },
        ]
    },
}


class TestPipelineCompleto(unittest.TestCase):
    """Ejecuta el programa entero contra un proyecto temporal."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        (self.base / "datos").mkdir()

    def tearDown(self) -> None:
        self._directorio.cleanup()

    def _escribir_patrones(self, contenido: str) -> None:
        (self.base / "patrones.txt").write_text(contenido, encoding="utf-8")

    def _escribir_datos(self, nombre: str, datos: object) -> None:
        (self.base / "datos" / nombre).write_text(
            json.dumps(datos, ensure_ascii=False), encoding="utf-8"
        )

    def _ejecutar(self, *extra: str) -> int:
        """Ejecuta el programa capturando la salida de consola.

        El logging se deja activo a propósito —estas pruebas comprueban que
        los archivos de log se escriben—, así que se redirige stderr para no
        ensuciar la salida del propio ejecutor de pruebas. El handler de
        consola se construye dentro de ``main()``, ya bajo la redirección.
        """
        self.consola = io.StringIO()
        with contextlib.redirect_stderr(self.consola):
            return aplicacion.main(["--base-dir", str(self.base), "--sin-progreso", *extra])

    def _resultados(self) -> dict[str, str]:
        carpeta = self.base / "resultados"
        return {
            ruta.name: ruta.read_text(encoding="utf-8") for ruta in sorted(carpeta.glob("*.txt"))
        }

    def test_ejecucion_completa_genera_los_resultados_esperados(self) -> None:
        self._escribir_patrones(
            "correos :: [a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\\.[a-zA-Z]{2,}\n"
            "urls :: https?://[^\\s]+\n"
        )
        self._escribir_datos("chat.json", _EXPORT_UN_CHAT)

        self.assertEqual(self._ejecutar(), EXITO)

        resultados = self._resultados()
        self.assertEqual(sorted(resultados), ["correos.txt", "urls.txt"])
        self.assertIn("ana@ejemplo.com", resultados["correos.txt"])
        self.assertIn("https://ejemplo.com/x", resultados["urls.txt"])
        self.assertIn("Chat: Chat con Ana", resultados["correos.txt"])
        self.assertIn("Usuario: Ana", resultados["correos.txt"])
        # El mensaje sin coincidencias no debe aparecer en ningún archivo.
        self.assertNotIn("sin nada interesante", "".join(resultados.values()))

    def test_crea_todas_las_carpetas_y_el_config_por_defecto(self) -> None:
        self._escribir_patrones("todo :: .\n")
        self._escribir_datos("chat.json", _EXPORT_UN_CHAT)

        self._ejecutar()

        for carpeta in ("datos", "resultados", "logs", "cache"):
            self.assertTrue((self.base / carpeta).is_dir(), carpeta)
        self.assertTrue((self.base / "config.json").is_file())

    def test_genera_los_dos_archivos_de_log(self) -> None:
        self._escribir_patrones("correos :: \\w+@\\w+\\.\\w+\n")
        self._escribir_datos("chat.json", _EXPORT_UN_CHAT)

        self._ejecutar()

        self.assertTrue((self.base / "logs" / "proceso.log").is_file())
        self.assertTrue((self.base / "logs" / "error.log").is_file())
        proceso = (self.base / "logs" / "proceso.log").read_text(encoding="utf-8")
        self.assertIn("Inicio de TelegramRegexSearch", proceso)
        self.assertIn("Fin de TelegramRegexSearch", proceso)
        self.assertIn("Resumen:", proceso)

    def test_los_regex_invalidos_se_registran_en_error_log(self) -> None:
        self._escribir_patrones("bueno :: \\w+@\\w+\\.\\w+\nmalo :: [sin_cerrar\n")
        self._escribir_datos("chat.json", _EXPORT_UN_CHAT)

        self.assertEqual(self._ejecutar(), EXITO)

        errores = (self.base / "logs" / "error.log").read_text(encoding="utf-8")
        self.assertIn("Regex inválido en la línea 2", errores)
        self.assertIn("bueno.txt", [p.name for p in (self.base / "resultados").glob("*.txt")])

    def test_soporta_el_export_completo_de_la_cuenta(self) -> None:
        self._escribir_patrones("correos :: [\\w.]+@[\\w.]+\\.\\w+\nurls :: https?://\\S+\n")
        self._escribir_datos("cuenta.json", _EXPORT_CUENTA_COMPLETA)

        self.assertEqual(self._ejecutar(), EXITO)

        resultados = self._resultados()
        self.assertIn("soporte@empresa.com", resultados["correos.txt"])
        self.assertIn("Chat: Grupo Trabajo", resultados["correos.txt"])
        self.assertIn("https://noticias.example", resultados["urls.txt"])
        self.assertIn("Chat: Canal Noticias", resultados["urls.txt"])
        self.assertIn("Usuario: channel999", resultados["urls.txt"])

    def test_procesa_varios_archivos_a_la_vez(self) -> None:
        self._escribir_patrones("correos :: [\\w.]+@[\\w.]+\\.\\w+\n")
        self._escribir_datos("uno.json", _EXPORT_UN_CHAT)
        self._escribir_datos("dos.json", _EXPORT_CUENTA_COMPLETA)

        self.assertEqual(self._ejecutar(), EXITO)

        contenido = self._resultados()["correos.txt"]
        self.assertIn("ana@ejemplo.com", contenido)
        self.assertIn("soporte@empresa.com", contenido)

    def test_procesa_tambien_exports_html(self) -> None:
        (self.base / "config.json").write_text(
            json.dumps({"extensiones_soportadas": [".json", ".html"]}), encoding="utf-8"
        )
        self._escribir_patrones("correos :: [\\w.]+@[\\w.]+\\.\\w+\n")
        (self.base / "datos" / "messages.html").write_text(
            '<div class="page_header"><div class="text bold">Chat HTML</div></div>'
            '<div class="message default clearfix" id="message7"><div class="body">'
            '<div class="from_name">Ana</div>'
            '<div class="text">escribe a html@ejemplo.com</div></div></div>',
            encoding="utf-8",
        )

        self.assertEqual(self._ejecutar(), EXITO)

        contenido = self._resultados()["correos.txt"]
        self.assertIn("html@ejemplo.com", contenido)
        self.assertIn("Chat: Chat HTML", contenido)

    def test_el_modo_por_consola_tiene_prioridad_sobre_config(self) -> None:
        (self.base / "config.json").write_text(
            json.dumps({"modo_busqueda": "search"}), encoding="utf-8"
        )
        self._escribir_patrones("palabras :: \\w+\n")
        self._escribir_datos(
            "chat.json",
            {"name": "C", "messages": [{"id": 1, "type": "message", "text": "uno dos tres"}]},
        )

        self.assertEqual(self._ejecutar("--modo", "finditer"), EXITO)

        contenido = self._resultados()["palabras.txt"]
        for palabra in ("uno", "dos", "tres"):
            self.assertIn(f"Coincidencia: {palabra}", contenido)

    def test_sin_patrones_devuelve_codigo_de_sin_trabajo(self) -> None:
        self._escribir_patrones("# solo comentarios\n\n")
        self._escribir_datos("chat.json", _EXPORT_UN_CHAT)
        self.assertEqual(self._ejecutar(), SIN_TRABAJO)

    def test_sin_datos_devuelve_codigo_de_sin_trabajo(self) -> None:
        self._escribir_patrones("correos :: \\w+@\\w+\n")
        self.assertEqual(self._ejecutar(), SIN_TRABAJO)

    def test_una_segunda_ejecucion_no_duplica_los_resultados(self) -> None:
        """Defecto detectado en la auditoría: los TXT se abrían en modo añadir."""
        self._escribir_patrones("correos :: [\\w.]+@[\\w.]+\\.\\w+\n")
        self._escribir_datos("chat.json", _EXPORT_UN_CHAT)

        self._ejecutar()
        primera = self._resultados()["correos.txt"]
        self._ejecutar()
        segunda = self._resultados()["correos.txt"]

        self.assertEqual(primera, segunda)
        self.assertEqual(segunda.count("ana@ejemplo.com"), primera.count("ana@ejemplo.com"))

    def test_respeta_carpetas_personalizadas_en_config(self) -> None:
        """Defecto detectado en la auditoría: solo se creaban las carpetas fijas."""
        (self.base / "config.json").write_text(
            json.dumps({"carpeta_resultados": "mis_resultados", "carpeta_cache": "mi_cache"}),
            encoding="utf-8",
        )
        self._escribir_patrones("correos :: [\\w.]+@[\\w.]+\\.\\w+\n")
        self._escribir_datos("chat.json", _EXPORT_UN_CHAT)

        self.assertEqual(self._ejecutar(), EXITO)

        self.assertTrue((self.base / "mi_cache").is_dir())
        self.assertTrue((self.base / "mis_resultados" / "correos.txt").is_file())

    def test_argumento_datos_apunta_a_otra_carpeta(self) -> None:
        otra = self.base / "exports_externos"
        otra.mkdir()
        (otra / "chat.json").write_text(json.dumps(_EXPORT_UN_CHAT), encoding="utf-8")
        self._escribir_patrones("correos :: [\\w.]+@[\\w.]+\\.\\w+\n")

        self.assertEqual(self._ejecutar("--datos", str(otra)), EXITO)
        self.assertIn("ana@ejemplo.com", self._resultados()["correos.txt"])

    def test_un_json_corrupto_no_impide_procesar_los_demas(self) -> None:
        self._escribir_patrones("correos :: [\\w.]+@[\\w.]+\\.\\w+\n")
        self._escribir_datos("bueno.json", _EXPORT_UN_CHAT)
        (self.base / "datos" / "roto.json").write_text('{"name": "X", "messa', encoding="utf-8")

        self.assertEqual(self._ejecutar(), EXITO)
        self.assertIn("ana@ejemplo.com", self._resultados()["correos.txt"])


if __name__ == "__main__":
    unittest.main()


class TestVentanasTemporales(unittest.TestCase):
    """Verifica el filtrado por rango de fechas de extremo a extremo."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        (self.base / "datos").mkdir()
        (self.base / "patrones.txt").write_text(
            "correos :: [\\w.]+@[\\w.]+\\.\\w+\n", encoding="utf-8"
        )
        self._escribir_datos_con_fechas()

    def tearDown(self) -> None:
        self._directorio.cleanup()

    def _escribir_datos_con_fechas(self) -> None:
        """Crea un chat con un mensaje por cada antigüedad de interés."""
        ahora = datetime.now()
        antiguedades = {"d10": 10, "d45": 45, "d75": 75, "d120": 120, "d200": 200}
        mensajes = [
            {
                "id": nombre,
                "type": "message",
                "date": (ahora - timedelta(days=dias)).strftime("%Y-%m-%dT%H:%M:%S"),
                "from": "Ana",
                "text": f"correo {nombre}@ejemplo.com",
            }
            for nombre, dias in antiguedades.items()
        ]
        (self.base / "datos" / "chat.json").write_text(
            json.dumps({"name": "Chat", "messages": mensajes}, ensure_ascii=False),
            encoding="utf-8",
        )

    def _ejecutar(self, *extra: str) -> int:
        with contextlib.redirect_stderr(io.StringIO()):
            return aplicacion.main(["--base-dir", str(self.base), "--sin-progreso", *extra])

    def _leer(self, *partes: str) -> str:
        return (self.base / "resultados" / Path(*partes)).read_text(encoding="utf-8")

    def test_sin_filtro_se_analiza_todo_el_historial(self) -> None:
        self.assertEqual(self._ejecutar(), EXITO)
        contenido = self._leer("correos.txt")
        for identificador in ("d10", "d45", "d75", "d120", "d200"):
            self.assertIn(f"{identificador}@ejemplo.com", contenido)

    def test_una_ventana_unica_escribe_en_la_carpeta_principal(self) -> None:
        self.assertEqual(self._ejecutar("--dias", "30"), EXITO)
        contenido = self._leer("correos.txt")
        self.assertIn("d10@ejemplo.com", contenido)
        self.assertNotIn("d45@ejemplo.com", contenido)
        self.assertNotIn("d200@ejemplo.com", contenido)

    def test_varias_ventanas_generan_una_subcarpeta_por_rango(self) -> None:
        self.assertEqual(self._ejecutar("--dias", "30,60,90,160,180"), EXITO)

        carpetas = sorted(p.name for p in (self.base / "resultados").iterdir() if p.is_dir())
        self.assertEqual(
            carpetas,
            [
                "ultimos_160_dias",
                "ultimos_180_dias",
                "ultimos_30_dias",
                "ultimos_60_dias",
                "ultimos_90_dias",
            ],
        )

    def test_las_ventanas_son_acumulativas(self) -> None:
        """Un mensaje de hace 10 días debe aparecer en las cinco ventanas."""
        self._ejecutar("--dias", "30,60,90,160,180")

        for carpeta in ("ultimos_30_dias", "ultimos_60_dias", "ultimos_180_dias"):
            self.assertIn("d10@ejemplo.com", self._leer(carpeta, "correos.txt"))

    def test_cada_ventana_excluye_lo_que_le_corresponde(self) -> None:
        self._ejecutar("--dias", "30,60,90,160,180")

        de_30 = self._leer("ultimos_30_dias", "correos.txt")
        self.assertNotIn("d45@ejemplo.com", de_30)

        de_60 = self._leer("ultimos_60_dias", "correos.txt")
        self.assertIn("d45@ejemplo.com", de_60)
        self.assertNotIn("d75@ejemplo.com", de_60)

        de_90 = self._leer("ultimos_90_dias", "correos.txt")
        self.assertIn("d75@ejemplo.com", de_90)
        self.assertNotIn("d120@ejemplo.com", de_90)

        de_180 = self._leer("ultimos_180_dias", "correos.txt")
        self.assertIn("d120@ejemplo.com", de_180)
        self.assertNotIn("d200@ejemplo.com", de_180)

    def test_el_rango_se_puede_fijar_en_config_json(self) -> None:
        """El requisito: el rango de fechas debe ser configurable."""
        (self.base / "config.json").write_text(
            json.dumps({"dias_recientes": [30, 60]}), encoding="utf-8"
        )
        self.assertEqual(self._ejecutar(), EXITO)

        self.assertIn("d10@ejemplo.com", self._leer("ultimos_30_dias", "correos.txt"))
        self.assertIn("d45@ejemplo.com", self._leer("ultimos_60_dias", "correos.txt"))

    def test_la_consola_tiene_prioridad_sobre_config_json(self) -> None:
        (self.base / "config.json").write_text(
            json.dumps({"dias_recientes": [180]}), encoding="utf-8"
        )
        self.assertEqual(self._ejecutar("--dias", "30"), EXITO)

        contenido = self._leer("correos.txt")
        self.assertIn("d10@ejemplo.com", contenido)
        self.assertNotIn("d120@ejemplo.com", contenido)

    def test_fechas_explicitas_desde_y_hasta(self) -> None:
        ahora = datetime.now()
        desde = (ahora - timedelta(days=80)).strftime("%Y-%m-%d")
        hasta = (ahora - timedelta(days=20)).strftime("%Y-%m-%d")

        self.assertEqual(self._ejecutar("--desde", desde, "--hasta", hasta), EXITO)

        contenido = self._leer("correos.txt")
        self.assertIn("d45@ejemplo.com", contenido)
        self.assertIn("d75@ejemplo.com", contenido)
        self.assertNotIn("d10@ejemplo.com", contenido)
        self.assertNotIn("d120@ejemplo.com", contenido)

    def test_una_fecha_mal_escrita_se_rechaza_con_claridad(self) -> None:
        self.assertEqual(self._ejecutar("--desde", "15/01/2024"), SIN_TRABAJO)

    def test_un_rango_invertido_se_rechaza(self) -> None:
        self.assertEqual(
            self._ejecutar("--desde", "2024-06-01", "--hasta", "2024-01-01"), SIN_TRABAJO
        )

    def test_las_coincidencias_incluyen_a_todos_los_usuarios(self) -> None:
        """El buscador no filtra por remitente: recoge lo de todos."""
        ahora = datetime.now().strftime("%Y-%m-%dT%H:%M:%S")
        mensajes = [
            {"id": 1, "type": "message", "date": ahora, "from": "Ana", "text": "a@x.com"},
            {"id": 2, "type": "message", "date": ahora, "from": "Beto", "text": "b@x.com"},
            {"id": 3, "type": "message", "date": ahora, "from": "Yo", "text": "c@x.com"},
        ]
        (self.base / "datos" / "grupo.json").write_text(
            json.dumps({"name": "Grupo", "messages": mensajes}), encoding="utf-8"
        )

        self.assertEqual(self._ejecutar("--dias", "30"), EXITO)

        contenido = self._leer("correos.txt")
        for usuario in ("Ana", "Beto", "Yo"):
            self.assertIn(f"Usuario: {usuario}", contenido)
