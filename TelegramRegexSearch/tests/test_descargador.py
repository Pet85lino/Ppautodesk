"""Pruebas del descargador opcional por MTProto.

Telethon no está instalado en el entorno de pruebas y una autenticación real
exigiría el código que Telegram envía al teléfono, así que aquí se verifica
lo que sí se puede verificar de forma determinista y sin red: la conversión
de mensajes al formato de export y la escritura del archivo resultante.

Esa es justamente la frontera que importa: si el archivo generado tiene el
formato correcto, el resto del pipeline —ya probado— lo procesa sin cambios.
"""

from __future__ import annotations

import json
import logging
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from core.descargador import (
    FILTROS_SERVIDOR,
    TIPOS_CHAT,
    ErrorDescarga,
    _obtener_filtro,
    coincide_con_dialogo,
    describir_dialogo,
    escribir_export,
    incluir_dialogo,
    mensaje_a_formato_export,
    telethon_disponible,
)
from core.parser_json import iterar_mensajes


class _RemitenteFalso:
    """Sustituto de una entidad de usuario o canal de Telethon."""

    def __init__(self, **atributos: object) -> None:
        for clave, valor in atributos.items():
            setattr(self, clave, valor)


class _MensajeFalso:
    """Sustituto de un objeto ``Message`` de Telethon."""

    def __init__(self, **atributos: object) -> None:
        self.id = 0
        self.message = ""
        self.date = None
        self.sender = None
        self.sender_id = None
        for clave, valor in atributos.items():
            setattr(self, clave, valor)


class TestConversionDeMensajes(unittest.TestCase):
    """Verifica la traducción de mensajes de Telethon al formato de export."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def test_convierte_un_mensaje_completo(self) -> None:
        mensaje = _MensajeFalso(
            id=42,
            message="hola mundo",
            date=datetime(2024, 3, 1, 12, 30, 0, tzinfo=timezone.utc),
            sender=_RemitenteFalso(first_name="Ana", last_name="Perez"),
            sender_id=999,
        )
        resultado = mensaje_a_formato_export(mensaje)
        self.assertIsNotNone(resultado)
        assert resultado is not None
        self.assertEqual(resultado["id"], 42)
        self.assertEqual(resultado["type"], "message")
        self.assertEqual(resultado["text"], "hola mundo")
        self.assertEqual(resultado["from"], "Ana Perez")
        self.assertEqual(resultado["from_id"], "999")
        self.assertEqual(resultado["date"], "2024-03-01T12:30:00")

    def test_descarta_mensajes_sin_texto(self) -> None:
        self.assertIsNone(mensaje_a_formato_export(_MensajeFalso(id=1, message="")))

    def test_usa_el_titulo_en_canales(self) -> None:
        mensaje = _MensajeFalso(
            id=1, message="post", sender=_RemitenteFalso(title="Mi Canal"), sender_id=-100
        )
        resultado = mensaje_a_formato_export(mensaje)
        assert resultado is not None
        self.assertEqual(resultado["from"], "Mi Canal")

    def test_recurre_al_usuario_si_no_hay_nombre(self) -> None:
        mensaje = _MensajeFalso(
            id=1, message="x", sender=_RemitenteFalso(username="anon123"), sender_id=5
        )
        resultado = mensaje_a_formato_export(mensaje)
        assert resultado is not None
        self.assertEqual(resultado["from"], "anon123")

    def test_remitente_ausente_deja_from_nulo(self) -> None:
        """El parser ya sabe recurrir a from_id cuando 'from' viene a null."""
        resultado = mensaje_a_formato_export(_MensajeFalso(id=1, message="x", sender_id=77))
        assert resultado is not None
        self.assertIsNone(resultado["from"])
        self.assertEqual(resultado["from_id"], "77")

    def test_normaliza_la_fecha_a_utc_sin_zona(self) -> None:
        zona = timezone(timedelta(hours=2))
        mensaje = _MensajeFalso(
            id=1, message="x", date=datetime(2024, 3, 1, 14, 0, 0, tzinfo=zona)
        )
        resultado = mensaje_a_formato_export(mensaje)
        assert resultado is not None
        self.assertEqual(resultado["date"], "2024-03-01T12:00:00")

    def test_fecha_ausente_no_produce_el_literal_none(self) -> None:
        resultado = mensaje_a_formato_export(_MensajeFalso(id=1, message="x", date=None))
        assert resultado is not None
        self.assertEqual(resultado["date"], "")


class TestEscribirExport(unittest.TestCase):
    """Verifica que el archivo generado sea un export válido para el pipeline."""

    def setUp(self) -> None:
        self._directorio = tempfile.TemporaryDirectory()
        self.base = Path(self._directorio.name)
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)
        self._directorio.cleanup()

    def _mensajes(self, cantidad: int) -> list[dict]:
        return [
            {
                "id": i,
                "type": "message",
                "date": "2024-03-01T12:00:00",
                "from": "Ana",
                "from_id": "1",
                "text": f"mensaje {i} con correo user{i}@ejemplo.com",
            }
            for i in range(cantidad)
        ]

    def test_genera_un_json_valido(self) -> None:
        ruta = self.base / "salida.json"
        escritos = escribir_export(ruta, "Mi Chat", "personal_chat", 7, iter(self._mensajes(3)))

        self.assertEqual(escritos, 3)
        datos = json.loads(ruta.read_text(encoding="utf-8"))
        self.assertEqual(datos["name"], "Mi Chat")
        self.assertEqual(datos["type"], "personal_chat")
        self.assertEqual(datos["id"], 7)
        self.assertEqual(len(datos["messages"]), 3)

    def test_el_archivo_generado_lo_lee_el_parser_del_proyecto(self) -> None:
        """La prueba clave: lo descargado entra en el pipeline sin adaptadores."""
        ruta = self.base / "descarga.json"
        escribir_export(ruta, "Chat Descargado", "descarga_mtproto", 9, iter(self._mensajes(5)))

        mensajes = list(iterar_mensajes(ruta))
        self.assertEqual(len(mensajes), 5)
        self.assertEqual(mensajes[0].chat, "Chat Descargado")
        self.assertEqual(mensajes[0].usuario, "Ana")
        self.assertIn("user0@ejemplo.com", mensajes[0].texto)

    def test_sin_mensajes_genera_un_export_vacio_valido(self) -> None:
        ruta = self.base / "vacio.json"
        self.assertEqual(escribir_export(ruta, "Vacio", "personal_chat", 1, iter([])), 0)
        self.assertEqual(json.loads(ruta.read_text(encoding="utf-8"))["messages"], [])
        self.assertEqual(list(iterar_mensajes(ruta)), [])

    def test_los_caracteres_especiales_sobreviven_al_viaje(self) -> None:
        ruta = self.base / "raro.json"
        mensajes = [
            {
                "id": 1,
                "type": "message",
                "date": "",
                "from": 'Ana "la jefa"',
                "from_id": "1",
                "text": 'comillas " barra \\ ñ emoji 🎉 salto\nlinea',
            }
        ]
        escribir_export(ruta, 'Chat "raro"', "personal_chat", 1, iter(mensajes))

        leidos = list(iterar_mensajes(ruta))
        self.assertEqual(leidos[0].texto, 'comillas " barra \\ ñ emoji 🎉 salto\nlinea')
        self.assertEqual(leidos[0].chat, 'Chat "raro"')
        self.assertEqual(leidos[0].usuario, 'Ana "la jefa"')

    def test_crea_la_carpeta_de_destino_si_no_existe(self) -> None:
        ruta = self.base / "nueva" / "sub" / "salida.json"
        escribir_export(ruta, "C", "personal_chat", 1, iter(self._mensajes(1)))
        self.assertTrue(ruta.is_file())


class TestFiltrosServidor(unittest.TestCase):
    """Verifica el mapeo de filtros de búsqueda del servidor."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def test_todos_no_aplica_filtro(self) -> None:
        self.assertIsNone(_obtener_filtro("todos"))

    def test_filtro_desconocido_lanza_error_claro(self) -> None:
        with self.assertRaises(ErrorDescarga):
            _obtener_filtro("inexistente")

    def test_los_filtros_documentados_estan_disponibles(self) -> None:
        for esperado in ("enlaces", "fotos", "videos", "documentos", "menciones", "fijados"):
            self.assertIn(esperado, FILTROS_SERVIDOR)

    @unittest.skipUnless(telethon_disponible(), "Telethon no está instalado")
    def test_los_nombres_de_filtro_existen_en_telethon(self) -> None:
        for clave in FILTROS_SERVIDOR:
            _obtener_filtro(clave)  # No debe lanzar.


class TestDegradacionSinTelethon(unittest.TestCase):
    """Verifica que la ausencia de Telethon no rompa el resto del proyecto."""

    def test_el_modulo_se_importa_sin_telethon_instalado(self) -> None:
        # El propio import de este archivo de pruebas ya lo demuestra; se deja
        # explícito porque es el requisito que mantiene el núcleo sin dependencias.
        import core.descargador as descargador

        self.assertTrue(hasattr(descargador, "descargar_historiales"))

    def test_telethon_disponible_devuelve_un_booleano(self) -> None:
        self.assertIsInstance(telethon_disponible(), bool)


if __name__ == "__main__":
    unittest.main()


class _DialogoFalso:
    """Sustituto de un objeto ``Dialog`` de Telethon."""

    def __init__(self, **atributos: object) -> None:
        self.id = 0
        self.name = ""
        self.is_group = False
        self.is_channel = False
        self.is_user = False
        self.entity = _RemitenteFalso()
        for clave, valor in atributos.items():
            setattr(self, clave, valor)


class TestSeleccionDeChats(unittest.TestCase):
    """Verifica el filtrado por tipo y la identificación de chats concretos."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)
        self.grupo = _DialogoFalso(id=101, name="Grupo Trabajo", is_group=True)
        # En Telegram un supergrupo es a la vez grupo y canal.
        self.supergrupo = _DialogoFalso(
            id=102, name="Super Grupo", is_group=True, is_channel=True
        )
        self.canal = _DialogoFalso(id=103, name="Canal Noticias", is_channel=True)
        self.privado = _DialogoFalso(id=104, name="Ana", is_user=True)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def test_tipo_todos_incluye_cualquier_dialogo(self) -> None:
        for dialogo in (self.grupo, self.supergrupo, self.canal, self.privado):
            self.assertTrue(incluir_dialogo(dialogo, "todos"))

    def test_tipo_grupos_incluye_los_supergrupos(self) -> None:
        self.assertTrue(incluir_dialogo(self.grupo, "grupos"))
        self.assertTrue(incluir_dialogo(self.supergrupo, "grupos"))
        self.assertFalse(incluir_dialogo(self.canal, "grupos"))
        self.assertFalse(incluir_dialogo(self.privado, "grupos"))

    def test_tipo_canales_excluye_los_supergrupos(self) -> None:
        """Un supergrupo tiene is_channel=True pero no es un canal de difusión."""
        self.assertTrue(incluir_dialogo(self.canal, "canales"))
        self.assertFalse(incluir_dialogo(self.supergrupo, "canales"))
        self.assertFalse(incluir_dialogo(self.grupo, "canales"))

    def test_tipo_privados(self) -> None:
        self.assertTrue(incluir_dialogo(self.privado, "privados"))
        self.assertFalse(incluir_dialogo(self.grupo, "privados"))

    def test_identifica_por_id_numerico(self) -> None:
        self.assertTrue(coincide_con_dialogo(self.grupo, "101"))
        self.assertFalse(coincide_con_dialogo(self.grupo, "999"))

    def test_identifica_por_nombre_de_usuario(self) -> None:
        canal = _DialogoFalso(
            id=1, name="Noticias", is_channel=True, entity=_RemitenteFalso(username="noticias")
        )
        self.assertTrue(coincide_con_dialogo(canal, "@noticias"))
        self.assertTrue(coincide_con_dialogo(canal, "noticias"))

    def test_identifica_grupos_privados_por_parte_del_nombre(self) -> None:
        """Los grupos privados no tienen @usuario: el nombre es la única vía."""
        self.assertTrue(coincide_con_dialogo(self.grupo, "trabajo"))
        self.assertTrue(coincide_con_dialogo(self.grupo, "Grupo Trabajo"))
        self.assertTrue(coincide_con_dialogo(self.grupo, "TRABAJO"))
        self.assertFalse(coincide_con_dialogo(self.grupo, "contabilidad"))

    def test_un_identificador_vacio_no_coincide_con_nada(self) -> None:
        self.assertFalse(coincide_con_dialogo(self.grupo, "   "))

    def test_describir_dialogo_clasifica_correctamente(self) -> None:
        self.assertEqual(describir_dialogo(self.grupo)["tipo"], "grupo")
        self.assertEqual(describir_dialogo(self.supergrupo)["tipo"], "grupo")
        self.assertEqual(describir_dialogo(self.canal)["tipo"], "canal")
        self.assertEqual(describir_dialogo(self.privado)["tipo"], "privado")

    def test_describir_dialogo_incluye_el_usuario_si_existe(self) -> None:
        canal = _DialogoFalso(
            id=5, name="N", is_channel=True, entity=_RemitenteFalso(username="canal5")
        )
        self.assertEqual(describir_dialogo(canal)["usuario"], "@canal5")
        self.assertEqual(describir_dialogo(self.grupo)["usuario"], "")

    def test_los_tipos_documentados_estan_disponibles(self) -> None:
        self.assertEqual(set(TIPOS_CHAT), {"todos", "grupos", "canales", "privados"})
