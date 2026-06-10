"""
core/analytics.py
-----------------
Analitica tecnica sobre el historico SQLite.

Indicadores (probabilisticos, mejoran con mas datos acumulados):
    * battery_health_score -> degradacion estimada de la bateria
      comparando los picos de carga recientes contra los historicos
      (health = capacidad_estimada / capacidad_nominal).
    * rssi_stats           -> media y varianza del RSSI registrado.
    * stability_score      -> 0-100 combinando varianza RSSI, jitter de
      latencia y eventos BLE (desconexiones, fallos de conexion).

Todas las funciones leen de DatabaseManager y devuelven dicts
serializables (listos para reportes y exportaciones).
"""

from __future__ import annotations

import logging
from statistics import mean, pvariance

logger = logging.getLogger("lino.core.analytics")

# Pesos de los componentes del stability_score (suman 1.0).
WEIGHT_RSSI = 0.4
WEIGHT_EVENTS = 0.35
WEIGHT_JITTER = 0.25

# Normalizadores: valor al que un componente se considera "totalmente malo".
RSSI_VARIANCE_WORST = 100.0   # dB^2 (sigma de 10 dB entre escaneos)
EVENTS_PER_SCAN_WORST = 0.5   # un evento adverso cada 2 escaneos
JITTER_WORST_MS = 50.0        # sigma de 50 ms en mediciones de latencia


def battery_health_score(db, mac: str) -> dict | None:
    """Salud estimada de la bateria de un dispositivo (0.0 - 1.0).

    Metodo: el pico de carga reciente (ultimo 25 % de lecturas) frente al
    pico historico. Un TWS sano alcanza ~100 % tras cargar; si con el
    tiempo sus maximos reportados caen, la celda esta degradada.

    Limite conocido: muchos TWS solo reportan porcentaje basico BLE, por
    lo que el score es una aproximacion y requiere lecturas en multiples
    sesiones de carga para ser representativo.
    """
    rows = db.battery_history(mac, limit=500)
    levels = [level for _, level in rows if level is not None]
    if len(levels) < 4:
        logger.info("Salud de bateria %s: datos insuficientes (%d)", mac, len(levels))
        return None

    historical_peak = max(levels)
    recent = levels[-max(2, len(levels) // 4):]
    recent_peak = max(recent)

    health = recent_peak / historical_peak if historical_peak else 0.0
    result = {
        "mac": mac,
        "samples": len(levels),
        "historical_peak": historical_peak,
        "recent_peak": recent_peak,
        "health_score": round(min(1.0, health), 3),
        "estimated_degradation_pct": round(max(0.0, (1.0 - health) * 100), 1),
    }
    logger.info(
        "Salud bateria %s: %.0f%% (pico reciente %d%% / historico %d%%)",
        mac, result["health_score"] * 100, recent_peak, historical_peak,
    )
    return result


def rssi_stats(db, mac: str) -> dict | None:
    """Media y varianza del RSSI registrado en scan_history."""
    try:
        rows = db._conn.execute(
            "SELECT rssi FROM scan_history WHERE mac = ? ORDER BY id DESC LIMIT 200",
            (mac,),
        ).fetchall()
    except Exception as exc:  # noqa: BLE001 - lectura defensiva
        logger.error("Error leyendo RSSI de %s: %s", mac, exc)
        return None

    values = [r[0] for r in rows if r[0] is not None]
    if len(values) < 2:
        return None
    return {
        "mac": mac,
        "samples": len(values),
        "mean_dbm": round(mean(values), 1),
        "variance": round(pvariance(values), 1),
    }


def _adverse_event_rate(db, mac: str) -> float:
    """Eventos BLE adversos por escaneo registrado (0.0 si no hay datos)."""
    try:
        events = db._conn.execute(
            """SELECT COUNT(*) FROM ble_events
               WHERE mac = ? AND event_type IN
                     ('disconnected', 'connect_failed', 'out_of_range')""",
            (mac,),
        ).fetchone()[0]
        scans = db._conn.execute(
            "SELECT COUNT(*) FROM scan_history WHERE mac = ?", (mac,)
        ).fetchone()[0]
    except Exception as exc:  # noqa: BLE001
        logger.error("Error contando eventos BLE de %s: %s", mac, exc)
        return 0.0
    return events / scans if scans else 0.0


def _latest_jitter_ms(db) -> float | None:
    """Ultimo jitter medido (la latencia es global, no por dispositivo)."""
    try:
        row = db._conn.execute(
            """SELECT jitter_ms FROM latency_history
               WHERE jitter_ms IS NOT NULL ORDER BY id DESC LIMIT 1"""
        ).fetchone()
        return float(row[0]) if row else None
    except Exception:  # noqa: BLE001
        return None


def stability_score(db, mac: str) -> dict:
    """Puntaje de estabilidad 0-100 del enlace con un dispositivo.

    Componentes (cada uno normalizado a 0.0-1.0, donde 1.0 = perfecto):
        rssi      -> varianza del RSSI (enlace fisicamente estable)
        events    -> tasa de desconexiones / fallos / fuera de rango
        jitter    -> estabilidad temporal del audio (si hay mediciones)

    Si falta un componente, su peso se redistribuye entre los presentes.
    """
    components: dict[str, float] = {}

    stats = rssi_stats(db, mac)
    if stats is not None:
        components["rssi"] = max(0.0, 1.0 - stats["variance"] / RSSI_VARIANCE_WORST)

    components["events"] = max(0.0, 1.0 - _adverse_event_rate(db, mac) / EVENTS_PER_SCAN_WORST)

    jitter = _latest_jitter_ms(db)
    if jitter is not None:
        components["jitter"] = max(0.0, 1.0 - jitter / JITTER_WORST_MS)

    weights = {"rssi": WEIGHT_RSSI, "events": WEIGHT_EVENTS, "jitter": WEIGHT_JITTER}
    present = {k: weights[k] for k in components}
    total_weight = sum(present.values())
    score = sum(components[k] * present[k] for k in components) / total_weight

    result = {
        "mac": mac,
        "score": round(score * 100, 1),
        "components": {k: round(v, 3) for k, v in components.items()},
    }
    logger.info("Estabilidad %s: %.1f/100 (%s)", mac, result["score"], result["components"])
    return result


def temperature_analytics(db, source: str | None = None) -> dict | None:
    """Analitica termica de las sesiones de carga (medidores USB).

    Temperaturas de carga altas o crecientes son el sintoma clasico de
    una bateria danada o de un circuito de carga defectuoso.

    Returns:
        dict {samples, max_c, mean_c, overheat_events, trend_c} o None
        si no hay datos de temperatura registrados.
    """
    query = "SELECT temp_c FROM charge_history WHERE temp_c IS NOT NULL"
    params: tuple = ()
    if source:
        query += " AND source = ?"
        params = (source,)
    query += " ORDER BY id ASC LIMIT 5000"

    try:
        rows = db._conn.execute(query, params).fetchall()
    except Exception as exc:  # noqa: BLE001
        logger.error("Error leyendo temperaturas: %s", exc)
        return None

    temps = [r[0] for r in rows]
    if len(temps) < 2:
        return None

    half = len(temps) // 2
    trend = mean(temps[half:]) - mean(temps[:half])
    result = {
        "samples": len(temps),
        "max_c": round(max(temps), 1),
        "mean_c": round(mean(temps), 1),
        "overheat_events": sum(1 for t in temps if t >= 45.0),
        "trend_c": round(trend, 1),  # positivo = calentandose entre mitades
    }
    logger.info(
        "Termica%s: max %.0f C, media %.0f C, sobrecalentamientos %d",
        f" {source}" if source else "", result["max_c"],
        result["mean_c"], result["overheat_events"],
    )
    return result


def full_report_data(db, mac: str) -> dict:
    """Paquete completo de analitica para el generador de reportes."""
    return {
        "battery_health": battery_health_score(db, mac),
        "rssi": rssi_stats(db, mac),
        "stability": stability_score(db, mac),
        "capabilities": db.get_fingerprint(mac),
        "temperature": temperature_analytics(db),
    }
