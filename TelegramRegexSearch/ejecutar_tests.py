"""Ejecuta la suite completa de pruebas del proyecto.

Pensado para poder auditar el proyecto sin instalar nada: usa únicamente
``unittest`` de la librería estándar, así que funciona igual en Windows y en
Pydroid 3.

Uso::

    python ejecutar_tests.py            # toda la suite
    python ejecutar_tests.py -v         # con el detalle de cada prueba
    python ejecutar_tests.py test_exportador   # solo un módulo
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

RAIZ = Path(__file__).resolve().parent

# Permite importar los paquetes del proyecto sea cual sea el directorio actual.
if str(RAIZ) not in sys.path:
    sys.path.insert(0, str(RAIZ))


def main(argv: list[str]) -> int:
    """Descubre y ejecuta las pruebas.

    Args:
        argv: Argumentos de consola. Admite ``-v`` y nombres de módulo.

    Returns:
        ``0`` si todas las pruebas pasan, ``1`` en caso contrario.
    """
    verbosidad = 2 if "-v" in argv or "--verbose" in argv else 1
    modulos = [arg for arg in argv if not arg.startswith("-")]

    cargador = unittest.TestLoader()
    if modulos:
        suite = cargador.loadTestsFromNames([f"tests.{nombre}" for nombre in modulos])
    else:
        suite = cargador.discover(str(RAIZ / "tests"), top_level_dir=str(RAIZ))

    resultado = unittest.TextTestRunner(verbosity=verbosidad).run(suite)

    print(
        f"\nResumen: {resultado.testsRun} pruebas | "
        f"{len(resultado.failures)} fallos | "
        f"{len(resultado.errors)} errores | "
        f"{len(resultado.skipped)} omitidas"
    )
    return 0 if resultado.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
