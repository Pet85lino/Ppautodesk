"""
core/comparison.py
------------------
Comparacion automatica entre dispositivos (o del mismo dispositivo en
ventanas de tiempo distintas).

    * compare_devices   -> dos TWS lado a lado, metrica por metrica.
    * compare_over_time -> mismo TWS: primera mitad del historico de
      bateria contra la segunda (deteccion de degradacion temporal).
"""

from __future__ import annotations

import logging
from statistics import mean

from core.scoring import device_quality_score

logger = logging.getLogger("lino.core.comparison")


def compare_devices(db, mac_a: str, mac_b: str) -> dict:
    """Comparacion metrica a metrica de dos dispositivos.

    Returns:
        dict con scores de ambos, delta por metrica y ganador global.
    """
    score_a = device_quality_score(db, mac_a)
    score_b = device_quality_score(db, mac_b)

    common = set(score_a["scores"]) & set(score_b["scores"])
    deltas = {
        metric: round(score_a["scores"][metric] - score_b["scores"][metric], 1)
        for metric in sorted(common)
    }
    winner = mac_a if score_a["overall"] >= score_b["overall"] else mac_b

    result = {
        "a": score_a,
        "b": score_b,
        "deltas": deltas,          # positivo = A mejor en esa metrica
        "winner": winner,
    }
    logger.info(
        "Comparacion %s (%.1f) vs %s (%.1f): gana %s",
        mac_a, score_a["overall"], mac_b, score_b["overall"], winner,
    )
    return result


def compare_over_time(db, mac: str) -> dict | None:
    """Evolucion del mismo dispositivo: mitad antigua vs mitad reciente
    del historico de bateria (tendencia de degradacion).

    Returns:
        dict con promedios/picos de cada mitad y la tendencia, o None
        si no hay historico suficiente (< 8 lecturas).
    """
    rows = db.battery_history(mac, limit=1000)
    levels = [level for _, level in rows if level is not None]
    if len(levels) < 8:
        logger.info("Comparacion temporal %s: historico insuficiente", mac)
        return None

    half = len(levels) // 2
    old, recent = levels[:half], levels[half:]

    old_peak, recent_peak = max(old), max(recent)
    trend_pct = (recent_peak - old_peak) / old_peak * 100.0 if old_peak else 0.0

    result = {
        "mac": mac,
        "samples": len(levels),
        "old": {"mean": round(mean(old), 1), "peak": old_peak},
        "recent": {"mean": round(mean(recent), 1), "peak": recent_peak},
        "peak_trend_pct": round(trend_pct, 1),  # negativo = degradacion
        "degrading": trend_pct < -2.0,
    }
    logger.info(
        "Tendencia %s: pico %d%% -> %d%% (%.1f%%)",
        mac, old_peak, recent_peak, trend_pct,
    )
    return result


def format_comparison(result: dict, name_a: str = "A", name_b: str = "B") -> str:
    """Tabla de texto lado a lado para la UI."""
    a, b = result["a"], result["b"]
    lines = [f"  {'Metrica':<12} {name_a:>8} {name_b:>8} {'delta':>7}"]
    for metric in sorted(set(a["scores"]) | set(b["scores"])):
        va = a["scores"].get(metric)
        vb = b["scores"].get(metric)
        delta = result["deltas"].get(metric)
        lines.append(
            f"  {metric:<12} "
            f"{va if va is not None else '--':>8} "
            f"{vb if vb is not None else '--':>8} "
            f"{f'{delta:+.1f}' if delta is not None else '--':>7}"
        )
    lines.append(f"  {'GLOBAL':<12} {a['overall']:>8} {b['overall']:>8}")
    lines.append(f"  Ganador: {result['winner']}")
    return "\n".join(lines)
