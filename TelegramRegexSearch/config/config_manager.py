"""Gestión de la configuración del proyecto (config.json).

Si el archivo no existe se crea con valores por defecto razonables, de modo
que el proyecto funcione sin intervención manual en la primera ejecución.

A diferencia de una carga "ingenua", aquí cada clave se valida por tipo y
por rango. Un valor inválido no se propaga al resto del programa: se
sustituye por el valor por defecto y se registra el problema. Así se evita
el peor escenario posible en una herramienta de búsqueda, que es terminar
con cero resultados sin que el usuario sepa por qué.
"""

from __future__ import annotations

import codecs
import json
import logging
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any

from core.models import MODOS_VALIDOS, ModoBusqueda

logger = logging.getLogger(__name__)

CONFIG_POR_DEFECTO: dict[str, Any] = {
    "carpeta_datos": "datos",
    "carpeta_resultados": "resultados",
    "carpeta_logs": "logs",
    "carpeta_cache": "cache",
    "archivo_patrones": "patrones.txt",
    "extensiones_soportadas": [".json"],
    "modo_busqueda": "search",
    "ignorar_mayusculas": False,
    "evitar_duplicados": True,
    "dias_recientes": [],
    "fecha_desde": "",
    "fecha_hasta": "",
    "actualizar_progreso_cada_n_mensajes": 200,
    "codificacion_salida": "utf-8",
    "max_archivos_resultado_abiertos": 32,
    "tam_buffer_lectura_kb": 256,
}

#: Extensiones que el proyecto sabe procesar realmente.
_EXTENSIONES_CONOCIDAS = frozenset({".json", ".html", ".htm"})


@dataclass(slots=True)
class Configuracion:
    """Configuración validada del proyecto.

    Usar un ``dataclass`` en lugar de un diccionario suelto permite que los
    errores de nombre de clave se detecten al escribir el código y no en
    mitad de una ejecución larga.
    """

    carpeta_datos: str = "datos"
    carpeta_resultados: str = "resultados"
    carpeta_logs: str = "logs"
    carpeta_cache: str = "cache"
    archivo_patrones: str = "patrones.txt"
    extensiones_soportadas: list[str] = field(default_factory=lambda: [".json"])
    modo_busqueda: ModoBusqueda = "search"
    ignorar_mayusculas: bool = False
    evitar_duplicados: bool = True
    dias_recientes: list[int] = field(default_factory=list)
    fecha_desde: str = ""
    fecha_hasta: str = ""
    actualizar_progreso_cada_n_mensajes: int = 200
    codificacion_salida: str = "utf-8"
    max_archivos_resultado_abiertos: int = 32
    tam_buffer_lectura_kb: int = 256

    def como_diccionario(self) -> dict[str, Any]:
        """Devuelve la configuración como diccionario serializable a JSON."""
        return {clave: getattr(self, clave) for clave in CONFIG_POR_DEFECTO}


def cargar_configuracion(ruta_config: Path) -> Configuracion:
    """Carga config.json, creándolo con valores por defecto si no existe.

    Las claves ausentes se completan con los valores por defecto y las
    inválidas se corrigen, reescribiendo el archivo para que el usuario vea
    la configuración efectiva que se está aplicando.

    Args:
        ruta_config: Ruta al archivo config.json.

    Returns:
        Instancia de :class:`Configuracion` ya validada.
    """
    if not ruta_config.exists():
        logger.info("config.json no encontrado; se crea con valores por defecto: %s", ruta_config)
        configuracion = Configuracion()
        _guardar_configuracion(ruta_config, configuracion.como_diccionario())
        return configuracion

    try:
        with ruta_config.open("r", encoding="utf-8") as archivo:
            datos_usuario = json.load(archivo)
    except (json.JSONDecodeError, OSError, UnicodeDecodeError) as exc:
        logger.error("No se pudo leer config.json (%s). Se usan valores por defecto.", exc)
        return Configuracion()

    if not isinstance(datos_usuario, dict):
        logger.error("config.json no contiene un objeto JSON. Se usan valores por defecto.")
        return Configuracion()

    for clave in datos_usuario:
        if clave not in CONFIG_POR_DEFECTO:
            logger.warning("Clave desconocida en config.json (se ignora): '%s'", clave)

    configuracion = _validar(datos_usuario)

    # Reescribe solo si la configuración efectiva difiere de la del archivo,
    # para no tocar el disco en cada ejecución.
    if configuracion.como_diccionario() != datos_usuario:
        _guardar_configuracion(ruta_config, configuracion.como_diccionario())

    return configuracion


def _validar(datos: dict[str, Any]) -> Configuracion:
    """Valida cada clave del diccionario y construye una :class:`Configuracion`."""
    return Configuracion(
        carpeta_datos=_texto(datos, "carpeta_datos"),
        carpeta_resultados=_texto(datos, "carpeta_resultados"),
        carpeta_logs=_texto(datos, "carpeta_logs"),
        carpeta_cache=_texto(datos, "carpeta_cache"),
        archivo_patrones=_texto(datos, "archivo_patrones"),
        extensiones_soportadas=_extensiones(datos),
        modo_busqueda=_modo_busqueda(datos),
        ignorar_mayusculas=_booleano(datos, "ignorar_mayusculas"),
        evitar_duplicados=_booleano(datos, "evitar_duplicados"),
        dias_recientes=_dias_recientes(datos),
        fecha_desde=_fecha(datos, "fecha_desde"),
        fecha_hasta=_fecha(datos, "fecha_hasta"),
        actualizar_progreso_cada_n_mensajes=_entero(
            datos, "actualizar_progreso_cada_n_mensajes", minimo=1, maximo=1_000_000
        ),
        codificacion_salida=_codificacion(datos),
        max_archivos_resultado_abiertos=_entero(
            datos, "max_archivos_resultado_abiertos", minimo=1, maximo=512
        ),
        tam_buffer_lectura_kb=_entero(datos, "tam_buffer_lectura_kb", minimo=4, maximo=8192),
    )


def _avisar_valor_invalido(clave: str, valor: Any) -> Any:
    """Registra un valor inválido y devuelve el valor por defecto de esa clave."""
    por_defecto = CONFIG_POR_DEFECTO[clave]
    logger.warning(
        "Valor inválido en config.json para '%s' (%r). Se usa el valor por defecto: %r",
        clave,
        valor,
        por_defecto,
    )
    return por_defecto


def _texto(datos: dict[str, Any], clave: str) -> str:
    """Valida que el valor sea una cadena no vacía."""
    valor = datos.get(clave, CONFIG_POR_DEFECTO[clave])
    if not isinstance(valor, str) or not valor.strip():
        return _avisar_valor_invalido(clave, valor)
    return valor.strip()


def _booleano(datos: dict[str, Any], clave: str) -> bool:
    """Valida que el valor sea un booleano estricto."""
    valor = datos.get(clave, CONFIG_POR_DEFECTO[clave])
    if not isinstance(valor, bool):
        return _avisar_valor_invalido(clave, valor)
    return valor


def _entero(datos: dict[str, Any], clave: str, minimo: int, maximo: int) -> int:
    """Valida que el valor sea un entero dentro del rango permitido."""
    valor = datos.get(clave, CONFIG_POR_DEFECTO[clave])
    # ``bool`` es subclase de ``int``: se excluye explícitamente.
    if not isinstance(valor, int) or isinstance(valor, bool) or not minimo <= valor <= maximo:
        return _avisar_valor_invalido(clave, valor)
    return valor


def _modo_busqueda(datos: dict[str, Any]) -> ModoBusqueda:
    """Valida que el modo de búsqueda sea uno de los soportados."""
    valor = datos.get("modo_busqueda", CONFIG_POR_DEFECTO["modo_busqueda"])
    if valor not in MODOS_VALIDOS:
        return _avisar_valor_invalido("modo_busqueda", valor)
    return valor


def _codificacion(datos: dict[str, Any]) -> str:
    """Valida que la codificación de salida exista realmente en Python."""
    valor = datos.get("codificacion_salida", CONFIG_POR_DEFECTO["codificacion_salida"])
    if not isinstance(valor, str):
        return _avisar_valor_invalido("codificacion_salida", valor)
    try:
        codecs.lookup(valor)
    except LookupError:
        return _avisar_valor_invalido("codificacion_salida", valor)
    return valor


def _extensiones(datos: dict[str, Any]) -> list[str]:
    """Valida la lista de extensiones soportadas, normalizándolas a minúsculas."""
    valor = datos.get("extensiones_soportadas", CONFIG_POR_DEFECTO["extensiones_soportadas"])
    if not isinstance(valor, list) or not valor:
        return list(_avisar_valor_invalido("extensiones_soportadas", valor))

    extensiones: list[str] = []
    for elemento in valor:
        if not isinstance(elemento, str) or not elemento.strip():
            logger.warning("Extensión inválida en config.json (se ignora): %r", elemento)
            continue
        extension = elemento.strip().lower()
        if not extension.startswith("."):
            extension = f".{extension}"
        if extension not in _EXTENSIONES_CONOCIDAS:
            logger.warning(
                "Extensión no soportada por el proyecto (se ignora): '%s'. Soportadas: %s",
                extension,
                ", ".join(sorted(_EXTENSIONES_CONOCIDAS)),
            )
            continue
        if extension not in extensiones:
            extensiones.append(extension)

    if not extensiones:
        return list(_avisar_valor_invalido("extensiones_soportadas", valor))
    return extensiones


def _dias_recientes(datos: dict[str, Any]) -> list[int]:
    """Valida la lista de ventanas temporales expresadas en días.

    Se admite tanto una lista (``[30, 60, 90]``) como un único número (``30``),
    porque escribir un entero suelto es el error de escritura más natural.
    """
    valor = datos.get("dias_recientes", CONFIG_POR_DEFECTO["dias_recientes"])

    if valor in (None, ""):
        return []
    if isinstance(valor, int) and not isinstance(valor, bool):
        valor = [valor]
    if not isinstance(valor, list):
        return list(_avisar_valor_invalido("dias_recientes", valor))

    dias: list[int] = []
    for elemento in valor:
        if not isinstance(elemento, int) or isinstance(elemento, bool) or elemento <= 0:
            logger.warning("Valor inválido en 'dias_recientes' (se ignora): %r", elemento)
            continue
        if elemento not in dias:
            dias.append(elemento)

    return sorted(dias)


def _fecha(datos: dict[str, Any], clave: str) -> str:
    """Valida una fecha en formato ``AAAA-MM-DD``, admitiendo la cadena vacía."""
    valor = datos.get(clave, CONFIG_POR_DEFECTO[clave])

    if valor in (None, ""):
        return ""
    if not isinstance(valor, str):
        return str(_avisar_valor_invalido(clave, valor))

    texto = valor.strip()
    try:
        datetime.strptime(texto, "%Y-%m-%d")
    except ValueError:
        logger.warning(
            "Fecha inválida en config.json para '%s' (%r). Se esperaba el "
            "formato AAAA-MM-DD; se ignora el filtro.",
            clave,
            valor,
        )
        return ""
    return texto


def _guardar_configuracion(ruta_config: Path, config: dict[str, Any]) -> None:
    """Escribe el diccionario de configuración en disco como JSON legible."""
    try:
        ruta_config.parent.mkdir(parents=True, exist_ok=True)
        with ruta_config.open("w", encoding="utf-8") as archivo:
            json.dump(config, archivo, indent=4, ensure_ascii=False)
            archivo.write("\n")
    except OSError as exc:
        logger.error("No se pudo escribir config.json: %s", exc)
