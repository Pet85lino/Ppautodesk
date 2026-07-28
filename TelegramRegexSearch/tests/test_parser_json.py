"""Pruebas del parser de exports JSON."""

from __future__ import annotations

import json
import logging
import tempfile
import unittest
from pathlib import Path

from core.parser_json import iterar_archivos, iterar_mensajes


class TestIterarMensajes(unittest.TestCase):
    """Verifica la normalización de los mensajes de Telegram."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def _escribir(self, mensajes: list[dict], nombre_chat: str = "Chat") -> Path:
        ruta = self.base / "export.json"
        ruta.write_text(
            json.dumps({"name": nombre_chat, "messages": mensajes}, ensure_ascii=False),
            encoding="utf-8",
        )
        return ruta

    def test_texto_como_cadena_simple(self) -> None:
        ruta = self._escribir(
            [{"id": 1, "type": "message", "date": "2024-01-01", "from": "Ana", "text": "hola"}]
        )
        mensajes = list(iterar_mensajes(ruta))
        self.assertEqual(len(mensajes), 1)
        self.assertEqual(mensajes[0].texto, "hola")
        self.assertEqual(mensajes[0].usuario, "Ana")
        self.assertEqual(mensajes[0].fecha, "2024-01-01")
        self.assertEqual(mensajes[0].id, "1")
        self.assertEqual(mensajes[0].chat, "Chat")
        self.assertEqual(mensajes[0].archivo, "export.json")

    def test_texto_como_lista_de_fragmentos_se_aplana(self) -> None:
        ruta = self._escribir(
            [
                {
                    "id": 2,
                    "type": "message",
                    "from": "Ana",
                    "text": [
                        "Mira ",
                        {"type": "link", "text": "https://ejemplo.com"},
                        " y ya",
                    ],
                }
            ]
        )
        mensajes = list(iterar_mensajes(ruta))
        self.assertEqual(mensajes[0].texto, "Mira https://ejemplo.com y ya")

    def test_descarta_mensajes_de_servicio(self) -> None:
        ruta = self._escribir(
            [
                {"id": 1, "type": "service", "action": "join_group_by_link", "text": ""},
                {"id": 2, "type": "message", "from": "Ana", "text": "hola"},
            ]
        )
        self.assertEqual([m.id for m in iterar_mensajes(ruta)], ["2"])

    def test_descarta_mensajes_sin_texto(self) -> None:
        ruta = self._escribir(
            [
                {"id": 1, "type": "message", "from": "Ana", "text": "", "photo": "foto.jpg"},
                {"id": 2, "type": "message", "from": "Ana", "text": "con pie de foto"},
            ]
        )
        self.assertEqual([m.id for m in iterar_mensajes(ruta)], ["2"])

    def test_remitente_nulo_usa_el_identificador(self) -> None:
        ruta = self._escribir(
            [{"id": 1, "type": "message", "from": None, "from_id": "user4242", "text": "x"}]
        )
        self.assertEqual(list(iterar_mensajes(ruta))[0].usuario, "user4242")

    def test_sin_remitente_ni_identificador_usa_desconocido(self) -> None:
        ruta = self._escribir([{"id": 1, "type": "message", "text": "x"}])
        self.assertEqual(list(iterar_mensajes(ruta))[0].usuario, "desconocido")

    def test_campos_ausentes_no_producen_el_literal_none(self) -> None:
        ruta = self._escribir([{"type": "message", "text": "x"}])
        mensaje = list(iterar_mensajes(ruta))[0]
        self.assertEqual(mensaje.fecha, "")
        self.assertEqual(mensaje.id, "")
        self.assertNotIn("None", (mensaje.fecha, mensaje.id))

    def test_sin_nombre_de_chat_usa_el_nombre_del_archivo(self) -> None:
        ruta = self.base / "resultado_chat.json"
        ruta.write_text(
            json.dumps({"messages": [{"id": 1, "type": "message", "text": "x"}]}),
            encoding="utf-8",
        )
        self.assertEqual(list(iterar_mensajes(ruta))[0].chat, "resultado_chat")

    def test_fragmentos_sin_clave_texto_se_ignoran(self) -> None:
        ruta = self._escribir(
            [{"id": 1, "type": "message", "text": ["a", {"type": "raro"}, {"text": "b"}]}]
        )
        self.assertEqual(list(iterar_mensajes(ruta))[0].texto, "ab")


class TestIterarArchivos(unittest.TestCase):
    """Verifica el descubrimiento de archivos de export."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def test_encuentra_archivos_recursivamente_y_filtra_por_extension(self) -> None:
        (self.base / "sub").mkdir()
        (self.base / "a.json").write_text("{}", encoding="utf-8")
        (self.base / "sub" / "b.JSON").write_text("{}", encoding="utf-8")
        (self.base / "c.txt").write_text("no", encoding="utf-8")
        (self.base / "d.html").write_text("<html></html>", encoding="utf-8")

        encontrados = [p.name for p in iterar_archivos(self.base, [".json"])]
        self.assertEqual(encontrados, ["a.json", "b.JSON"])

    def test_acepta_varias_extensiones(self) -> None:
        (self.base / "a.json").write_text("{}", encoding="utf-8")
        (self.base / "b.html").write_text("<html></html>", encoding="utf-8")
        encontrados = sorted(p.name for p in iterar_archivos(self.base, [".json", ".html"]))
        self.assertEqual(encontrados, ["a.json", "b.html"])

    def test_carpeta_inexistente_no_lanza(self) -> None:
        self.assertEqual(list(iterar_archivos(self.base / "no_existe", [".json"])), [])


if __name__ == "__main__":
    unittest.main()
