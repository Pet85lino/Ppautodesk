"""Pruebas del lector JSON incremental.

Estas pruebas son las que respaldan la afirmación de que el proyecto no
carga el archivo entero en memoria: todas se ejecutan con un tamaño de
bloque diminuto (8 KiB como mínimo real, forzado a valores pequeños), de
modo que cualquier dependencia oculta de "tener el archivo completo en el
buffer" fallaría.
"""

from __future__ import annotations

import json
import logging
import tempfile
import unittest
from pathlib import Path

from core.json_stream import iterar_mensajes_crudos


class TestIterarMensajesCrudos(unittest.TestCase):
    """Verifica el recorrido en streaming de los exports JSON."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def _escribir_json(self, datos: object, nombre: str = "export.json") -> Path:
        ruta = self.base / nombre
        ruta.write_text(json.dumps(datos, ensure_ascii=False), encoding="utf-8")
        return ruta

    def test_export_de_un_solo_chat(self) -> None:
        ruta = self._escribir_json(
            {
                "name": "Mi Chat",
                "type": "personal_chat",
                "id": 123,
                "messages": [
                    {"id": 1, "type": "message", "text": "hola"},
                    {"id": 2, "type": "message", "text": "adios"},
                ],
            }
        )
        resultado = list(iterar_mensajes_crudos(ruta, tam_bloque=16))
        self.assertEqual(len(resultado), 2)
        self.assertEqual([nombre for nombre, _ in resultado], ["Mi Chat", "Mi Chat"])
        self.assertEqual([m["text"] for _, m in resultado], ["hola", "adios"])

    def test_export_completo_de_la_cuenta_con_varios_chats(self) -> None:
        ruta = self._escribir_json(
            {
                "about": "Export completo",
                "personal_information": {"first_name": "Peter", "last_name": "L"},
                "chats": {
                    "about": "Chats",
                    "list": [
                        {
                            "name": "Chat A",
                            "type": "personal_chat",
                            "messages": [{"id": 1, "type": "message", "text": "de A"}],
                        },
                        {
                            "name": "Chat B",
                            "type": "private_group",
                            "messages": [{"id": 2, "type": "message", "text": "de B"}],
                        },
                    ],
                },
            }
        )
        resultado = list(iterar_mensajes_crudos(ruta, tam_bloque=16))
        self.assertEqual(
            [(nombre, m["text"]) for nombre, m in resultado],
            [("Chat A", "de A"), ("Chat B", "de B")],
        )

    def test_texto_que_imita_la_clave_messages_no_desincroniza_el_escaner(self) -> None:
        """Un mensaje cuyo texto contiene `"messages": [` no debe confundir al escáner."""
        trampa = 'mira esto: "messages": [{"type": "message"}] <- literal'
        ruta = self._escribir_json(
            {
                "name": "Trampa",
                "messages": [
                    {"id": 1, "type": "message", "text": trampa},
                    {"id": 2, "type": "message", "text": "posterior"},
                ],
            }
        )
        resultado = list(iterar_mensajes_crudos(ruta, tam_bloque=16))
        self.assertEqual(len(resultado), 2)
        self.assertEqual(resultado[0][1]["text"], trampa)
        self.assertEqual(resultado[1][1]["text"], "posterior")

    def test_comillas_escapadas_y_secuencias_unicode(self) -> None:
        texto = 'dijo \\"hola\\" y luego ñoño \\\\ fin'
        ruta = self.base / "raro.json"
        ruta.write_text(
            '{"name": "Raro", "messages": [{"id": 1, "type": "message", "text": "'
            + texto
            + '"}]}',
            encoding="utf-8",
        )
        resultado = list(iterar_mensajes_crudos(ruta, tam_bloque=8))
        self.assertEqual(len(resultado), 1)
        self.assertEqual(resultado[0][1]["text"], 'dijo "hola" y luego ñoño \\ fin')

    def test_array_de_mensajes_vacio(self) -> None:
        ruta = self._escribir_json({"name": "Vacio", "messages": []})
        self.assertEqual(list(iterar_mensajes_crudos(ruta, tam_bloque=16)), [])

    def test_sin_clave_messages_no_produce_nada(self) -> None:
        ruta = self._escribir_json({"name": "Nada", "otros": [1, 2, 3]})
        self.assertEqual(list(iterar_mensajes_crudos(ruta, tam_bloque=16)), [])

    def test_json_truncado_se_registra_y_no_lanza(self) -> None:
        ruta = self.base / "truncado.json"
        ruta.write_text('{"name": "X", "messages": [{"id": 1, "type": "mess', encoding="utf-8")
        # Lo ya leído se conserva; el error se registra sin propagarse.
        self.assertEqual(list(iterar_mensajes_crudos(ruta, tam_bloque=16)), [])

    def test_archivo_inexistente_no_lanza(self) -> None:
        self.assertEqual(list(iterar_mensajes_crudos(self.base / "no.json")), [])

    def test_nombre_nulo_no_rompe_el_recorrido(self) -> None:
        ruta = self._escribir_json(
            {"name": None, "messages": [{"id": 1, "type": "message", "text": "x"}]}
        )
        resultado = list(iterar_mensajes_crudos(ruta, tam_bloque=16))
        self.assertEqual(len(resultado), 1)
        self.assertIsNone(resultado[0][0])

    def test_recorre_muchos_mensajes_con_bloque_minusculo(self) -> None:
        """Con 2000 mensajes y bloques de 64 bytes, el recorrido debe ser exacto."""
        mensajes = [
            {"id": i, "type": "message", "text": f"mensaje numero {i} " + "x" * 50}
            for i in range(2000)
        ]
        ruta = self._escribir_json({"name": "Grande", "messages": mensajes})
        resultado = list(iterar_mensajes_crudos(ruta, tam_bloque=64))
        self.assertEqual(len(resultado), 2000)
        self.assertEqual(resultado[0][1]["id"], 0)
        self.assertEqual(resultado[-1][1]["id"], 1999)

    def test_elementos_no_objeto_se_ignoran(self) -> None:
        ruta = self.base / "mixto.json"
        ruta.write_text(
            '{"name": "M", "messages": [1, "texto", {"id": 9, "type": "message"}, null]}',
            encoding="utf-8",
        )
        resultado = list(iterar_mensajes_crudos(ruta, tam_bloque=16))
        self.assertEqual(len(resultado), 1)
        self.assertEqual(resultado[0][1]["id"], 9)


if __name__ == "__main__":
    unittest.main()
