"""Verificación (y, si hace falta, instalación) de los requisitos del proyecto.

El núcleo de TelegramRegexSearch funciona **solo con la librería estándar**,
así que en una instalación normal no hay nada que instalar: este módulo se
limita a comprobar que la versión de Python es suficiente y que los módulos
estándar que se usan están realmente disponibles. Esa comprobación no es
superflua: algunas distribuciones recortadas de Python para Android omiten
piezas como ``sqlite3`` o ``lzma``, y conviene detectarlo con un mensaje
claro en lugar de con un ``ImportError`` a mitad del proceso.

Para funciones opcionales que sí requieren paquetes externos (por ejemplo,
descargar historiales en vivo con Telethon) se ofrece
:func:`asegurar_paquetes`, que comprueba qué falta y lo instala con pip bajo
petición explícita.
"""

from __future__ import annotations

import importlib.util
import logging
import re
import subprocess
import sys
from dataclasses import dataclass

logger = logging.getLogger(__name__)

#: Versión mínima de Python soportada.
VERSION_MINIMA: tuple[int, int] = (3, 10)

#: Módulos de la librería estándar que el proyecto usa y que podrían faltar
#: en distribuciones recortadas de Python.
MODULOS_ESTANDAR_REQUERIDOS: tuple[str, ...] = (
    "argparse",
    "codecs",
    "collections",
    "dataclasses",
    "html.parser",
    "json",
    "logging",
    "pathlib",
    "re",
    "shutil",
    "time",
    "typing",
)

#: Nombres de paquete aceptables en pip. Impide que una cadena como
#: "--upgrade" o "paquete; rm -rf /" se cuele como opción de la herramienta.
_NOMBRE_PAQUETE_VALIDO = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*(\[[A-Za-z0-9._,-]+\])?$")


@dataclass(frozen=True, slots=True)
class Requisito:
    """Paquete externo opcional.

    Attributes:
        modulo: Nombre con el que se importa (``telethon``).
        paquete: Nombre con el que se instala en pip (``Telethon``).
        motivo: Para qué lo necesita el proyecto, usado en los mensajes.
    """

    modulo: str
    paquete: str
    motivo: str


def verificar_version_python() -> bool:
    """Comprueba que el intérprete cumpla la versión mínima.

    Returns:
        ``True`` si la versión es suficiente.
    """
    if sys.version_info < VERSION_MINIMA:
        logger.error(
            "Se requiere Python %d.%d o superior; en uso: %s",
            VERSION_MINIMA[0],
            VERSION_MINIMA[1],
            sys.version.split()[0],
        )
        return False

    logger.debug("Version de Python: %s", sys.version.split()[0])
    return True


def modulo_disponible(nombre_modulo: str) -> bool:
    """Indica si un módulo puede importarse, sin llegar a ejecutarlo.

    Args:
        nombre_modulo: Nombre del módulo a comprobar.

    Returns:
        ``True`` si el módulo está disponible.
    """
    try:
        return importlib.util.find_spec(nombre_modulo) is not None
    except (ImportError, ValueError):
        return False


def verificar_modulos_estandar() -> list[str]:
    """Comprueba los módulos estándar requeridos.

    Returns:
        Lista con los nombres de los módulos que faltan (vacía si está todo).
    """
    faltantes = [nombre for nombre in MODULOS_ESTANDAR_REQUERIDOS if not modulo_disponible(nombre)]
    if faltantes:
        logger.error(
            "Faltan módulos de la librería estándar: %s. "
            "Tu instalación de Python está incompleta.",
            ", ".join(faltantes),
        )
    return faltantes


def verificar_entorno() -> bool:
    """Comprueba todos los requisitos necesarios para la ejecución básica.

    Returns:
        ``True`` si el entorno puede ejecutar el proyecto.
    """
    if not verificar_version_python():
        return False
    if verificar_modulos_estandar():
        return False
    logger.debug("Entorno verificado: se cumplen todos los requisitos básicos.")
    return True


def pip_disponible() -> bool:
    """Indica si pip puede invocarse en el intérprete actual.

    En Pydroid 3 y en algunos Python embebidos, pip no está expuesto como
    módulo ejecutable.
    """
    if not sys.executable:
        return False
    return modulo_disponible("pip")


def asegurar_paquetes(
    requisitos: list[Requisito],
    instalar_automaticamente: bool = False,
    tiempo_limite: int = 600,
) -> list[Requisito]:
    """Verifica paquetes externos opcionales e instala los que falten.

    La instalación nunca ocurre por defecto: modificar el entorno de Python
    del usuario a sus espaldas es una sorpresa desagradable, y en Pydroid 3
    puede dejar la instalación en un estado inconsistente. Por eso hay que
    pedirla explícitamente con ``instalar_automaticamente=True``.

    Args:
        requisitos: Paquetes externos a comprobar.
        instalar_automaticamente: Si es ``True``, instala los que falten.
        tiempo_limite: Segundos máximos concedidos a pip.

    Returns:
        Lista de requisitos que siguen sin estar disponibles al terminar.
    """
    faltantes = [req for req in requisitos if not modulo_disponible(req.modulo)]
    if not faltantes:
        return []

    for req in faltantes:
        logger.warning("Falta el paquete '%s' (%s).", req.paquete, req.motivo)

    comando_manual = f"{sys.executable or 'python'} -m pip install " + " ".join(
        req.paquete for req in faltantes
    )

    if not instalar_automaticamente:
        logger.info("Para instalarlos manualmente ejecuta:\n    %s", comando_manual)
        return faltantes

    if not pip_disponible():
        logger.error(
            "pip no está disponible en este intérprete, así que no se pueden "
            "instalar los paquetes automáticamente. Instálalos a mano:\n    %s",
            comando_manual,
        )
        return faltantes

    instalables = [req for req in faltantes if _nombre_seguro(req.paquete)]
    if not instalables:
        return faltantes

    logger.info("Instalando con pip: %s", ", ".join(req.paquete for req in instalables))
    if not _ejecutar_pip([req.paquete for req in instalables], tiempo_limite):
        return faltantes

    # ``find_spec`` consulta cachés de importación que no conocen los paquetes
    # recién instalados; hay que invalidarlas antes de volver a comprobar.
    importlib.invalidate_caches()
    return [req for req in faltantes if not modulo_disponible(req.modulo)]


def _nombre_seguro(paquete: str) -> bool:
    """Valida el nombre de un paquete antes de pasárselo a pip."""
    if _NOMBRE_PAQUETE_VALIDO.match(paquete):
        return True
    logger.error("Nombre de paquete no válido, no se instalará: %r", paquete)
    return False


def _ejecutar_pip(paquetes: list[str], tiempo_limite: int) -> bool:
    """Invoca ``pip install`` y registra el resultado.

    Returns:
        ``True`` si pip terminó correctamente.
    """
    # ``--`` separa las opciones de los nombres de paquete: aunque la
    # validación previa ya lo impide, así ningún nombre puede interpretarse
    # como opción de pip.
    comando = [sys.executable, "-m", "pip", "install", "--disable-pip-version-check", "--"]
    comando.extend(paquetes)

    try:
        resultado = subprocess.run(  # noqa: S603 - comando fijo, nombres validados
            comando,
            capture_output=True,
            text=True,
            timeout=tiempo_limite,
            check=False,
        )
    except subprocess.TimeoutExpired:
        logger.error("La instalación con pip superó el tiempo límite de %ds.", tiempo_limite)
        return False
    except OSError as exc:
        logger.error("No se pudo ejecutar pip: %s", exc)
        return False

    if resultado.returncode != 0:
        logger.error(
            "pip terminó con código %d. Detalle:\n%s",
            resultado.returncode,
            (resultado.stderr or resultado.stdout or "").strip()[:2000],
        )
        return False

    logger.info("Paquetes instalados correctamente.")
    return True
