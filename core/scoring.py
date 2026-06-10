"""
core/scoring.py
---------------
Device Scoring Engine: "Overall Device Quality Score".

Convierte la analitica historica en puntajes 0-100 por area:

    Metrica      Fuente
    ----------   ------------------------------------------
    estabilidad  analytics.stability_score (RSSI/eventos/jitter)
    bateria      analytics.battery_health_score
    latencia     ultima medicion de latency_history
    audio        jitter persistido + extras frescos (RMS, dropouts)

El puntaje global es el promedio ponderado de las metricas presentes;
si una metrica no tiene datos, su peso se redistribuye (sin castigar
dispositivos con menos historico).
"""

from __future__ import annotations

import logging

from core import analytics

logger = logging.getLogger("lino.core.scoring")

WEIGHTS = {
    "estabilidad": 0.30,
    "bateria": 0.25,
    "latencia": 0.25,
    "audio": 0.20,
}


def latency_score(latency_ms: float) -> float:
    """0-100: 60 ms o menos = perfecto; cae 0.3 puntos por ms extra."""
    return max(0.0, min(100.0, 100.0 - max(0.0, latency_ms - 60.0) * 0.3))


def jitter_score(std_ms: float) -> float:
    """0-100: cada ms de desviacion estandar cuesta 2 puntos."""
    return max(0.0, 100.0 - std_ms * 2.0)


def rms_balance_score(diff_db: float) -> float:
    """0-100: cada dB de desbalance L/R cuesta 12 puntos."""
    return max(0.0, 100.0 - abs(diff_db) * 12.0)


def continuity_score(continuity_pct: float) -> float:
    """0-100: la continuidad ya es un porcentaje; cuadratica para castigar
    huecos (98 % de continuidad NO es un 98: hay dropouts audibles)."""
    return max(0.0, min(100.0, (continuity_pct / 100.0) ** 2 * 100.0))


def _latest_latency(db) -> dict | None:
    return db.latest_latency()


def device_quality_score(db, mac: str, extras: dict | None = None) -> dict:
    """Puntaje de calidad global de un dispositivo.

    Args:
        db: DatabaseManager.
        mac: dispositivo a puntuar.
        extras: resultados frescos opcionales del pipeline de diagnostico:
            {"rms_diff_db": float, "continuity_pct": float}

    Returns:
        {"scores": {metrica: 0-100}, "overall": 0-100, "missing": [...]}
    """
    extras = extras or {}
    scores: dict[str, float] = {}

    stability = analytics.stability_score(db, mac)
    scores["estabilidad"] = stability["score"]

    health = analytics.battery_health_score(db, mac)
    if health is not None:
        scores["bateria"] = round(health["health_score"] * 100.0, 1)

    latest = _latest_latency(db)
    if latest and latest["latency_ms"] is not None:
        scores["latencia"] = round(latency_score(latest["latency_ms"]), 1)

    # Audio: combina los componentes disponibles (jitter persistido,
    # balance RMS y continuidad si llegan frescos del diagnostico).
    audio_parts: list[float] = []
    if latest and latest.get("jitter_ms") is not None:
        audio_parts.append(jitter_score(latest["jitter_ms"]))
    if "rms_diff_db" in extras:
        audio_parts.append(rms_balance_score(extras["rms_diff_db"]))
    if "continuity_pct" in extras:
        audio_parts.append(continuity_score(extras["continuity_pct"]))
    if audio_parts:
        scores["audio"] = round(sum(audio_parts) / len(audio_parts), 1)

    present = {k: WEIGHTS[k] for k in scores}
    total_weight = sum(present.values())
    overall = sum(scores[k] * present[k] for k in scores) / total_weight

    result = {
        "mac": mac,
        "scores": scores,
        "overall": round(overall, 1),
        "missing": sorted(set(WEIGHTS) - set(scores)),
    }
    logger.info(
        "Score %s: overall %.1f/100 %s (sin datos: %s)",
        mac, result["overall"], scores, result["missing"] or "ninguna",
    )
    return result


def format_score(result: dict) -> str:
    """Tabla de texto para la UI / reportes."""
    lines = [f"  {metric:<12} {value:>5.1f}/100" for metric, value in result["scores"].items()]
    if result["missing"]:
        lines.append(f"  (sin datos: {', '.join(result['missing'])})")
    lines.append(f"  {'GLOBAL':<12} {result['overall']:>5.1f}/100")
    return "\n".join(lines)
