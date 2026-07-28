"""Menú interactivo para entornos sin línea de comandos.

En Pydroid 3 el botón de ejecutar lanza el script **sin argumentos**, así que
todas las opciones de consola quedan fuera de alcance. Sin un menú, desde el
móvil solo se podría hacer una cosa: analizar lo que ya hubiera en ``datos/``.

Este módulo presenta las mismas acciones que las opciones de consola, pero
eligiéndolas por número. No sustituye a los argumentos: cuando se pasa
cualquiera, el menú no aparece y el programa se comporta como siempre, de
modo que los usos automatizados no cambian.

Solo usa ``input()`` y ``print()``, así que funciona igual en la terminal de
Windows y en la de Pydroid 3.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Callable

logger = logging.getLogger(__name__)

_ANCHO = 46


@dataclass(frozen=True, slots=True)
class OpcionMenu:
    """Entrada del menú.

    Attributes:
        clave: Número que teclea el usuario.
        titulo: Texto mostrado.
        accion: Función a ejecutar; devuelve ``True`` para seguir en el menú.
    """

    clave: str
    titulo: str
    accion: Callable[[], bool]


def elegir_opcion(entrada: str, opciones: list[OpcionMenu]) -> OpcionMenu | None:
    """Busca la opción que corresponde a lo tecleado.

    Se acepta el número con espacios alrededor, que es un desliz habitual
    escribiendo en un teclado táctil.

    Args:
        entrada: Texto introducido por el usuario.
        opciones: Opciones disponibles.

    Returns:
        La opción elegida, o ``None`` si no corresponde a ninguna.
    """
    limpia = entrada.strip()
    for opcion in opciones:
        if opcion.clave == limpia:
            return opcion
    return None


def pedir_texto(mensaje: str, por_defecto: str = "") -> str:
    """Pide un texto mostrando el valor que se usará si se deja en blanco."""
    sufijo = f" [{por_defecto}]" if por_defecto else ""
    try:
        respuesta = input(f"{mensaje}{sufijo}: ").strip()
    except (EOFError, KeyboardInterrupt):
        return por_defecto
    return respuesta or por_defecto


def pedir_entero(mensaje: str, por_defecto: int | None = None) -> int | None:
    """Pide un número entero positivo, admitiendo dejarlo en blanco.

    Returns:
        El número introducido, o ``por_defecto`` si se deja vacío o no es
        un número válido.
    """
    texto = pedir_texto(mensaje, "" if por_defecto is None else str(por_defecto))
    if not texto:
        return por_defecto
    try:
        valor = int(texto)
    except ValueError:
        print("  (No es un número; se ignora.)")
        return por_defecto
    if valor <= 0:
        print("  (Debe ser mayor que cero; se ignora.)")
        return por_defecto
    return valor


def pedir_opcion_de_lista(mensaje: str, opciones: list[str], por_defecto: str) -> str:
    """Pide elegir un valor de una lista cerrada, por número o por nombre."""
    print(f"\n{mensaje}")
    for indice, opcion in enumerate(opciones, start=1):
        marca = "  <- por defecto" if opcion == por_defecto else ""
        print(f"  {indice}. {opcion}{marca}")

    respuesta = pedir_texto("Elige", por_defecto)

    if respuesta.isdigit():
        indice = int(respuesta) - 1
        if 0 <= indice < len(opciones):
            return opciones[indice]
        print(f"  (Fuera de rango; se usa '{por_defecto}'.)")
        return por_defecto

    if respuesta in opciones:
        return respuesta

    print(f"  (No reconocido; se usa '{por_defecto}'.)")
    return por_defecto


def confirmar(mensaje: str, por_defecto: bool = True) -> bool:
    """Pide una confirmación de sí o no."""
    indicador = "S/n" if por_defecto else "s/N"
    try:
        respuesta = input(f"{mensaje} [{indicador}]: ").strip().lower()
    except (EOFError, KeyboardInterrupt):
        return False
    if not respuesta:
        return por_defecto
    return respuesta[0] in ("s", "y")


def mostrar_cabecera(titulo: str) -> None:
    """Imprime una cabecera enmarcada."""
    print(f"\n{'=' * _ANCHO}\n {titulo}\n{'=' * _ANCHO}")


def bucle_menu(titulo: str, opciones: list[OpcionMenu]) -> None:
    """Muestra el menú repetidamente hasta que se elija salir.

    Args:
        titulo: Encabezado del menú.
        opciones: Opciones disponibles; la acción devuelve ``False`` para
            terminar el bucle.
    """
    while True:
        mostrar_cabecera(titulo)
        for opcion in opciones:
            print(f" {opcion.clave}. {opcion.titulo}")
        print()

        try:
            entrada = input("Elige una opción: ")
        except (EOFError, KeyboardInterrupt):
            print("\nHasta luego.")
            return

        elegida = elegir_opcion(entrada, opciones)
        if elegida is None:
            print("\nEsa opción no existe. Teclea uno de los números de la lista.")
            continue

        try:
            if not elegida.accion():
                return
        except KeyboardInterrupt:
            print("\n\nOperación cancelada. Vuelves al menú.")
        except Exception as exc:  # noqa: BLE001 - el menú nunca debe caerse
            logger.exception("Error ejecutando la opción '%s'", elegida.titulo)
            print(f"\nAlgo ha fallado: {exc}\nEl detalle está en logs/error.log.")
