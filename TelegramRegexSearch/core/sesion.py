"""Conexión y autenticación contra Telegram mediante MTProto.

Este módulo resuelve el inicio de sesión completo desde la propia aplicación:
pide el teléfono, el código de verificación que envía Telegram y, si la cuenta
la tiene activada, la contraseña de verificación en dos pasos. No hace falta
ningún cliente de escritorio ni exportar nada a mano.

La sesión se guarda en un archivo ``.session`` dentro de la carpeta de caché,
de modo que solo se pide el código la primera vez. **Ese archivo da acceso a
la cuenta**: está excluido del repositorio y no debe compartirse.

Diseño para poder probarlo
--------------------------
La entrada por consola está detrás de :class:`Solicitante` y el cliente se
recibe ya construido, así que el flujo de autenticación —que es donde está la
lógica delicada— se puede probar sin Telethon, sin red y sin un teléfono real.
"""

from __future__ import annotations

import getpass
import logging
from pathlib import Path
from typing import Any, Protocol

from utils.credenciales import CredencialesTelegram

logger = logging.getLogger(__name__)

#: Intentos permitidos al introducir el código de verificación.
_INTENTOS_CODIGO = 3

#: Nombre del archivo de sesión dentro de la carpeta de caché.
_NOMBRE_SESION = "telegram"


class ErrorAutenticacion(Exception):
    """No se pudo completar el inicio de sesión en Telegram."""


class Solicitante(Protocol):
    """Origen de los datos que hay que pedirle a la persona usuaria."""

    def texto(self, mensaje: str) -> str:
        """Pide un dato visible (teléfono, código)."""

    def secreto(self, mensaje: str) -> str:
        """Pide un dato que no debe mostrarse en pantalla (contraseña)."""

    def aviso(self, mensaje: str) -> None:
        """Muestra información al usuario."""


class SolicitanteConsola:
    """Implementación de :class:`Solicitante` sobre la consola estándar.

    Funciona igual en la terminal de Windows y en la de Pydroid 3.
    """

    def texto(self, mensaje: str) -> str:
        """Lee una línea de la entrada estándar."""
        return input(mensaje).strip()

    def secreto(self, mensaje: str) -> str:
        """Lee una contraseña sin mostrarla, si el entorno lo permite.

        Algunas consolas embebidas —Pydroid 3 entre ellas— no ofrecen una
        entrada oculta. En ese caso se avisa y se lee de forma normal, que es
        preferible a dejar al usuario sin poder iniciar sesión.
        """
        try:
            return getpass.getpass(mensaje).strip()
        except (getpass.GetPassWarning, OSError, EOFError):
            print("(Esta consola no permite ocultar la escritura.)")
            return input(mensaje).strip()

    def aviso(self, mensaje: str) -> None:
        """Escribe un mensaje informativo por consola."""
        print(mensaje)


def _es_error(excepcion: BaseException, nombre_clase: str) -> bool:
    """Indica si una excepción es de la clase de Telethon indicada.

    Se compara por nombre en lugar de importar las clases porque Telethon es
    una dependencia opcional: así este módulo se puede importar y probar sin
    tenerlo instalado, y el código no se rompe si Telethon reorganiza dónde
    vive cada excepción entre versiones.
    """
    return any(clase.__name__ == nombre_clase for clase in type(excepcion).__mro__)


async def autenticar(cliente: Any, solicitante: Solicitante) -> Any:
    """Completa el inicio de sesión de un cliente ya conectado.

    Si la sesión guardada sigue siendo válida no pregunta nada. En caso
    contrario pide el teléfono, envía el código y lo solicita, contemplando
    la contraseña de verificación en dos pasos.

    Args:
        cliente: Cliente de Telethon ya conectado.
        solicitante: Origen de los datos que hay que pedir al usuario.

    Returns:
        El propio cliente, ya autenticado.

    Raises:
        ErrorAutenticacion: Si no se puede completar el inicio de sesión.
    """
    if await cliente.is_user_authorized():
        logger.info("Sesión existente reutilizada: no hace falta volver a identificarse.")
        return cliente

    solicitante.aviso(
        "\nPrimer inicio de sesión en Telegram.\n"
        "Se enviará un código a tu aplicación de Telegram (no por SMS, salvo\n"
        "que no tengas ninguna sesión abierta)."
    )

    telefono = solicitante.texto(
        "\nTeléfono con prefijo internacional (por ejemplo +34600111222): "
    )
    if not telefono:
        raise ErrorAutenticacion("No se indicó ningún número de teléfono.")

    try:
        await cliente.send_code_request(telefono)
    except Exception as exc:  # noqa: BLE001 - se traduce a un mensaje claro
        raise ErrorAutenticacion(f"No se pudo enviar el código a {telefono}: {exc}") from exc

    solicitante.aviso("\nCódigo enviado. Revisa los mensajes de Telegram.")

    for intento in range(1, _INTENTOS_CODIGO + 1):
        codigo = solicitante.texto("Código de verificación: ")
        if not codigo:
            continue

        try:
            await cliente.sign_in(phone=telefono, code=codigo)
            return await _confirmar(cliente, solicitante)
        except Exception as exc:  # noqa: BLE001 - se distingue por tipo más abajo
            if _es_error(exc, "SessionPasswordNeededError"):
                return await _iniciar_con_contrasena(cliente, solicitante)
            if _es_error(exc, "PhoneCodeInvalidError"):
                restantes = _INTENTOS_CODIGO - intento
                if restantes:
                    solicitante.aviso(f"Código incorrecto. Te quedan {restantes} intento(s).")
                    continue
                raise ErrorAutenticacion("Código incorrecto demasiadas veces.") from exc
            if _es_error(exc, "PhoneCodeExpiredError"):
                raise ErrorAutenticacion(
                    "El código ha caducado. Vuelve a ejecutar el comando para pedir uno nuevo."
                ) from exc
            raise ErrorAutenticacion(f"No se pudo iniciar sesión: {exc}") from exc

    raise ErrorAutenticacion("No se introdujo ningún código válido.")


async def _iniciar_con_contrasena(cliente: Any, solicitante: Solicitante) -> Any:
    """Completa el inicio de sesión en cuentas con verificación en dos pasos."""
    solicitante.aviso("\nEsta cuenta tiene verificación en dos pasos activada.")

    contrasena = solicitante.secreto("Contraseña de verificación en dos pasos: ")
    if not contrasena:
        raise ErrorAutenticacion("No se indicó la contraseña de verificación en dos pasos.")

    try:
        await cliente.sign_in(password=contrasena)
    except Exception as exc:  # noqa: BLE001 - se traduce a un mensaje claro
        if _es_error(exc, "PasswordHashInvalidError"):
            raise ErrorAutenticacion("La contraseña de verificación es incorrecta.") from exc
        raise ErrorAutenticacion(f"No se pudo iniciar sesión: {exc}") from exc

    return await _confirmar(cliente, solicitante)


async def _confirmar(cliente: Any, solicitante: Solicitante) -> Any:
    """Confirma la identidad de la cuenta con la que se ha iniciado sesión."""
    try:
        yo = await cliente.get_me()
    except Exception:  # noqa: BLE001 - la confirmación es informativa, no crítica
        solicitante.aviso("\nSesión iniciada correctamente.")
        return cliente

    nombre = getattr(yo, "first_name", None) or getattr(yo, "username", None) or "tu cuenta"
    solicitante.aviso(f"\nSesión iniciada como {nombre}.")
    logger.info("Autenticación completada.")
    return cliente


async def conectar(
    credenciales: CredencialesTelegram,
    carpeta_sesion: Path,
    solicitante: Solicitante | None = None,
) -> Any:
    """Crea un cliente de Telethon y lo deja autenticado y listo para usar.

    Args:
        credenciales: ``api_id`` y ``api_hash`` de la aplicación.
        carpeta_sesion: Carpeta donde guardar el archivo de sesión.
        solicitante: Origen de los datos a pedir; por defecto, la consola.

    Returns:
        Cliente de Telethon conectado y autenticado. Es responsabilidad del
        llamante desconectarlo al terminar.

    Raises:
        ErrorAutenticacion: Si no se puede conectar o iniciar sesión.
    """
    from telethon import TelegramClient

    carpeta_sesion.mkdir(parents=True, exist_ok=True)
    ruta_sesion = carpeta_sesion / _NOMBRE_SESION

    cliente = TelegramClient(str(ruta_sesion), credenciales.api_id, credenciales.api_hash)

    try:
        await cliente.connect()
    except Exception as exc:  # noqa: BLE001 - se traduce a un mensaje accionable
        raise ErrorAutenticacion(
            f"No se pudo conectar con Telegram: {exc}. Comprueba tu conexión a internet."
        ) from exc

    try:
        return await autenticar(cliente, solicitante or SolicitanteConsola())
    except BaseException:
        await cliente.disconnect()
        raise
