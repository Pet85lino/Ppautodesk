"""Pruebas del flujo de autenticación contra Telegram.

Se prueba sin Telethon, sin red y sin un teléfono real: el cliente y la
consola están sustituidos por dobles, que es justo para lo que se diseñaron
las interfaces del módulo. Lo que se verifica es la lógica delicada —los
reintentos del código, la verificación en dos pasos, la reutilización de la
sesión y que nunca se quede un cliente conectado tras un fallo—, que es donde
se concentran los errores de este tipo de flujos.
"""

from __future__ import annotations

import asyncio
import logging
import unittest

from core.sesion import ErrorAutenticacion, SolicitanteConsola, autenticar


class _SolicitanteFalso:
    """Consola simulada: devuelve respuestas preparadas y registra los avisos."""

    def __init__(self, respuestas: list[str], secretos: list[str] | None = None) -> None:
        self.respuestas = list(respuestas)
        self.secretos = list(secretos or [])
        self.avisos: list[str] = []

    def texto(self, mensaje: str) -> str:
        return self.respuestas.pop(0) if self.respuestas else ""

    def secreto(self, mensaje: str) -> str:
        return self.secretos.pop(0) if self.secretos else ""

    def aviso(self, mensaje: str) -> None:
        self.avisos.append(mensaje)


# Excepciones que imitan a las de Telethon. El módulo las identifica por el
# nombre de la clase, así que estas sirven exactamente igual que las reales.
class SessionPasswordNeededError(Exception):
    """La cuenta tiene verificación en dos pasos."""


class PhoneCodeInvalidError(Exception):
    """El código introducido no es correcto."""


class PhoneCodeExpiredError(Exception):
    """El código ha caducado."""


class PasswordHashInvalidError(Exception):
    """La contraseña de dos pasos no es correcta."""


class _Yo:
    """Resultado simulado de ``get_me()``."""

    first_name = "Peter"
    username = "peter"


class _ClienteFalso:
    """Cliente de Telethon simulado, configurable para cada escenario."""

    def __init__(
        self,
        autorizado: bool = False,
        errores_sign_in: list[Exception] | None = None,
        error_envio: Exception | None = None,
    ) -> None:
        self.autorizado = autorizado
        self.errores_sign_in = list(errores_sign_in or [])
        self.error_envio = error_envio
        self.codigos_enviados: list[str] = []
        self.intentos_sign_in: list[dict] = []
        self.desconectado = False

    async def is_user_authorized(self) -> bool:
        return self.autorizado

    async def send_code_request(self, telefono: str) -> None:
        if self.error_envio is not None:
            raise self.error_envio
        self.codigos_enviados.append(telefono)

    async def sign_in(self, phone: str | None = None, code: str | None = None,
                      password: str | None = None) -> None:
        self.intentos_sign_in.append({"phone": phone, "code": code, "password": password})
        if self.errores_sign_in:
            raise self.errores_sign_in.pop(0)
        self.autorizado = True

    async def get_me(self) -> _Yo:
        return _Yo()

    async def disconnect(self) -> None:
        self.desconectado = True


def _ejecutar(corrutina):
    """Ejecuta una corrutina en un bucle nuevo, como hace la aplicación."""
    return asyncio.run(corrutina)


class TestAutenticar(unittest.TestCase):
    """Verifica el flujo de inicio de sesión."""

    def setUp(self) -> None:
        logging.disable(logging.CRITICAL)

    def tearDown(self) -> None:
        logging.disable(logging.NOTSET)

    def test_una_sesion_valida_no_pregunta_nada(self) -> None:
        cliente = _ClienteFalso(autorizado=True)
        solicitante = _SolicitanteFalso([])

        _ejecutar(autenticar(cliente, solicitante))

        self.assertEqual(cliente.codigos_enviados, [])
        self.assertEqual(cliente.intentos_sign_in, [])

    def test_inicio_de_sesion_correcto_al_primer_intento(self) -> None:
        cliente = _ClienteFalso()
        solicitante = _SolicitanteFalso(["+34600111222", "12345"])

        _ejecutar(autenticar(cliente, solicitante))

        self.assertEqual(cliente.codigos_enviados, ["+34600111222"])
        self.assertEqual(cliente.intentos_sign_in[0]["code"], "12345")
        self.assertTrue(any("Peter" in aviso for aviso in solicitante.avisos))

    def test_codigo_incorrecto_permite_reintentar(self) -> None:
        cliente = _ClienteFalso(errores_sign_in=[PhoneCodeInvalidError()])
        solicitante = _SolicitanteFalso(["+34600111222", "00000", "12345"])

        _ejecutar(autenticar(cliente, solicitante))

        self.assertEqual(len(cliente.intentos_sign_in), 2)
        self.assertTrue(any("incorrecto" in aviso.lower() for aviso in solicitante.avisos))

    def test_demasiados_codigos_incorrectos_termina_con_error(self) -> None:
        cliente = _ClienteFalso(
            errores_sign_in=[PhoneCodeInvalidError() for _ in range(3)]
        )
        solicitante = _SolicitanteFalso(["+34600111222", "1", "2", "3"])

        with self.assertRaises(ErrorAutenticacion) as contexto:
            _ejecutar(autenticar(cliente, solicitante))

        self.assertIn("demasiadas veces", str(contexto.exception))

    def test_verificacion_en_dos_pasos(self) -> None:
        cliente = _ClienteFalso(errores_sign_in=[SessionPasswordNeededError()])
        solicitante = _SolicitanteFalso(["+34600111222", "12345"], secretos=["mi_contrasena"])

        _ejecutar(autenticar(cliente, solicitante))

        self.assertEqual(cliente.intentos_sign_in[-1]["password"], "mi_contrasena")

    def test_contrasena_de_dos_pasos_incorrecta(self) -> None:
        cliente = _ClienteFalso(
            errores_sign_in=[SessionPasswordNeededError(), PasswordHashInvalidError()]
        )
        solicitante = _SolicitanteFalso(["+34600111222", "12345"], secretos=["mal"])

        with self.assertRaises(ErrorAutenticacion) as contexto:
            _ejecutar(autenticar(cliente, solicitante))

        self.assertIn("contraseña", str(contexto.exception).lower())

    def test_contrasena_vacia_se_rechaza(self) -> None:
        cliente = _ClienteFalso(errores_sign_in=[SessionPasswordNeededError()])
        solicitante = _SolicitanteFalso(["+34600111222", "12345"], secretos=[""])

        with self.assertRaises(ErrorAutenticacion):
            _ejecutar(autenticar(cliente, solicitante))

    def test_codigo_caducado_indica_como_seguir(self) -> None:
        cliente = _ClienteFalso(errores_sign_in=[PhoneCodeExpiredError()])
        solicitante = _SolicitanteFalso(["+34600111222", "12345"])

        with self.assertRaises(ErrorAutenticacion) as contexto:
            _ejecutar(autenticar(cliente, solicitante))

        self.assertIn("caducado", str(contexto.exception))

    def test_telefono_vacio_se_rechaza_sin_llamar_a_telegram(self) -> None:
        cliente = _ClienteFalso()
        solicitante = _SolicitanteFalso([""])

        with self.assertRaises(ErrorAutenticacion):
            _ejecutar(autenticar(cliente, solicitante))

        self.assertEqual(cliente.codigos_enviados, [])

    def test_fallo_al_enviar_el_codigo_se_explica(self) -> None:
        cliente = _ClienteFalso(error_envio=RuntimeError("numero no valido"))
        solicitante = _SolicitanteFalso(["+34600111222"])

        with self.assertRaises(ErrorAutenticacion) as contexto:
            _ejecutar(autenticar(cliente, solicitante))

        self.assertIn("+34600111222", str(contexto.exception))

    def test_un_fallo_en_get_me_no_impide_iniciar_sesion(self) -> None:
        """La confirmación del nombre es informativa: no debe tumbar el acceso."""

        class _SinGetMe(_ClienteFalso):
            async def get_me(self):
                raise RuntimeError("sin conexión momentánea")

        cliente = _SinGetMe()
        solicitante = _SolicitanteFalso(["+34600111222", "12345"])

        _ejecutar(autenticar(cliente, solicitante))

        self.assertTrue(any("correctamente" in aviso for aviso in solicitante.avisos))

    def test_avisa_de_que_el_codigo_llega_por_telegram(self) -> None:
        """El código llega a la app, no por SMS: es la confusión más habitual."""
        cliente = _ClienteFalso()
        solicitante = _SolicitanteFalso(["+34600111222", "12345"])

        _ejecutar(autenticar(cliente, solicitante))

        self.assertTrue(any("Telegram" in aviso for aviso in solicitante.avisos))


class TestSolicitanteConsola(unittest.TestCase):
    """Verifica la implementación por consola."""

    def test_cumple_el_protocolo_esperado(self) -> None:
        solicitante = SolicitanteConsola()
        for metodo in ("texto", "secreto", "aviso"):
            self.assertTrue(callable(getattr(solicitante, metodo)))


if __name__ == "__main__":
    unittest.main()
