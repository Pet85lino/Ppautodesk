"""Pruebas del parser de exports HTML."""

from __future__ import annotations

import logging
import tempfile
import unittest
from pathlib import Path

from core.parser_html import iterar_mensajes_html

_HTML_BASE = """<!DOCTYPE html>
<html><body>
<div class="page_wrap">
  <div class="page_header">
    <div class="content">
      <div class="text bold">Chat de Prueba</div>
    </div>
  </div>
  <div class="page_body chat_page">
    <div class="history">
      <div class="message service" id="message-1">
        <div class="body details">1 January 2024</div>
      </div>
      <div class="message default clearfix" id="message1">
        <div class="body">
          <div class="pull_right date details" title="01.01.2024 10:00:00 UTC+01:00">10:00</div>
          <div class="from_name">Ana</div>
          <div class="text">Hola, mi correo es ana@ejemplo.com</div>
        </div>
      </div>
      <div class="message default clearfix joined" id="message2">
        <div class="body">
          <div class="pull_right date details" title="01.01.2024 10:01:00 UTC+01:00">10:01</div>
          <div class="text">Segundo mensaje seguido</div>
        </div>
      </div>
      <div class="message default clearfix" id="message3">
        <div class="body">
          <div class="pull_right date details" title="01.01.2024 10:02:00 UTC+01:00">10:02</div>
          <div class="from_name">Beto</div>
          <div class="text">Mira <a href="https://ejemplo.com">este enlace</a> por favor</div>
        </div>
      </div>
      <div class="message default clearfix" id="message4">
        <div class="body">
          <div class="from_name">Ana</div>
          <div class="text">Primera linea<br>Segunda linea</div>
        </div>
      </div>
      <div class="message default clearfix" id="message5">
        <div class="body">
          <div class="from_name">Ana</div>
          <div class="media_wrap clearfix"><div class="media clearfix pull_left">foto</div></div>
        </div>
      </div>
    </div>
  </div>
</div>
</body></html>
"""


class TestIterarMensajesHtml(unittest.TestCase):
    """Verifica la extracción de mensajes desde el HTML de Telegram."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        self.ruta = self.base / "messages.html"
        self.ruta.write_text(_HTML_BASE, encoding="utf-8")
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def _mensajes(self, tam_bloque: int = 256 * 1024):
        return list(iterar_mensajes_html(self.ruta, tam_bloque))

    def test_extrae_los_mensajes_con_texto(self) -> None:
        mensajes = self._mensajes()
        self.assertEqual([m.id for m in mensajes], ["1", "2", "3", "4"])

    def test_descarta_mensajes_de_servicio_y_sin_texto(self) -> None:
        textos = [m.texto for m in self._mensajes()]
        self.assertNotIn("1 January 2024", textos)
        self.assertEqual(len(textos), 4)

    def test_toma_el_nombre_del_chat_de_la_cabecera(self) -> None:
        self.assertTrue(all(m.chat == "Chat de Prueba" for m in self._mensajes()))

    def test_la_fecha_sale_del_atributo_title(self) -> None:
        self.assertEqual(self._mensajes()[0].fecha, "01.01.2024 10:00:00 UTC+01:00")

    def test_los_mensajes_joined_heredan_el_remitente(self) -> None:
        mensajes = self._mensajes()
        self.assertEqual(mensajes[0].usuario, "Ana")
        self.assertEqual(mensajes[1].usuario, "Ana")
        self.assertEqual(mensajes[2].usuario, "Beto")

    def test_el_texto_de_los_enlaces_se_conserva(self) -> None:
        self.assertEqual(self._mensajes()[2].texto, "Mira este enlace por favor")

    def test_los_saltos_de_linea_se_preservan(self) -> None:
        self.assertEqual(self._mensajes()[3].texto, "Primera linea\nSegunda linea")

    def test_el_resultado_es_identico_leyendo_en_bloques_diminutos(self) -> None:
        """El parser alimenta HTMLParser por trozos: el corte no debe alterar nada."""
        completo = [(m.id, m.texto, m.usuario) for m in self._mensajes()]
        troceado = [(m.id, m.texto, m.usuario) for m in self._mensajes(tam_bloque=1)]
        self.assertEqual(completo, troceado)

    def test_html_sin_mensajes_no_lanza(self) -> None:
        ruta = self.base / "vacio.html"
        ruta.write_text("<html><body><p>nada</p></body></html>", encoding="utf-8")
        self.assertEqual(list(iterar_mensajes_html(ruta)), [])

    def test_archivo_inexistente_no_lanza(self) -> None:
        self.assertEqual(list(iterar_mensajes_html(self.base / "no.html")), [])

    def test_entidades_html_se_decodifican(self) -> None:
        ruta = self.base / "entidades.html"
        ruta.write_text(
            '<div class="message default clearfix" id="message9"><div class="body">'
            '<div class="from_name">Ana</div>'
            '<div class="text">1 &lt; 2 &amp;&amp; 3 &gt; 2</div></div></div>',
            encoding="utf-8",
        )
        self.assertEqual(list(iterar_mensajes_html(ruta))[0].texto, "1 < 2 && 3 > 2")


if __name__ == "__main__":
    unittest.main()
