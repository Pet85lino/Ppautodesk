"""
core/versioning.py
------------------
Versionado de los artefactos analiticos (reportes, sesiones, exports).

Los algoritmos de scoring/analytics evolucionan: dos reportes solo son
comparables si se conoce la version del motor que los genero. Cada
artefacto incluye este bloque de metadatos.

Reglas de incremento:
    * REPORT_SCHEMA_VERSION -> cambia la ESTRUCTURA del reporte/sesion.
    * SCORING_VERSION       -> cambian formulas o pesos del scoring.
    * ANALYTICS_VERSION     -> cambian los algoritmos de analitica.
"""

from __future__ import annotations

REPORT_SCHEMA_VERSION = "1.0"
SCORING_VERSION = "1.0"
ANALYTICS_VERSION = "1.1"  # 1.1: + temperature_analytics


def engine_metadata(app_version: str | None = None) -> dict:
    """Bloque de metadatos que se adjunta a reportes y sesiones."""
    return {
        "schema_version": REPORT_SCHEMA_VERSION,
        "analysis_engine": f"lino-audio-diagnostic/{app_version or 'dev'}",
        "scoring_version": SCORING_VERSION,
        "analytics_version": ANALYTICS_VERSION,
    }
