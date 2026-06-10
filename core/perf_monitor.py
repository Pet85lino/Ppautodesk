"""
core/perf_monitor.py
--------------------
Monitor de memoria y rendimiento para sesiones largas (overnight).

Combina:
    * psutil  -> RSS del proceso (crecimiento = fuga o acumulacion).
    * tracemalloc -> top de asignaciones Python por linea de codigo
      (identifica QUE esta creciendo: deques, grabaciones WAV, charts).

Activacion: config.json -> perf.monitor_enabled = true, o explicita
desde scripts (soak tests). Cada muestra se loguea y queda en el dict
`samples` para analisis posterior.
"""

from __future__ import annotations

import logging
import time
import tracemalloc

import psutil
from PySide6.QtCore import QObject, QTimer

logger = logging.getLogger("lino.core.perf")

TOP_ALLOCATIONS = 5


class PerfMonitor(QObject):
    """Muestreo periodico de RSS + asignaciones Python."""

    def __init__(self, interval_s: float = 60.0, parent: QObject | None = None):
        super().__init__(parent)
        self._interval_ms = int(interval_s * 1000)
        self._process = psutil.Process()
        self._timer: QTimer | None = None
        self.samples: list[dict] = []
        self._baseline_rss: int | None = None

    def start(self) -> None:
        """Arranca tracemalloc y el muestreo periodico (hilo principal)."""
        tracemalloc.start()
        self._baseline_rss = self._process.memory_info().rss
        self._timer = QTimer(self)
        self._timer.setInterval(self._interval_ms)
        self._timer.timeout.connect(self.sample)
        self._timer.start()
        logger.info(
            "PerfMonitor iniciado (cada %.0f s, RSS base %.1f MB)",
            self._interval_ms / 1000, self._baseline_rss / 1e6,
        )

    def stop(self) -> None:
        if self._timer:
            self._timer.stop()
        if tracemalloc.is_tracing():
            tracemalloc.stop()
        logger.info("PerfMonitor detenido (%d muestras)", len(self.samples))

    def sample(self) -> dict:
        """Toma una muestra: RSS, delta desde el arranque y top allocs."""
        rss = self._process.memory_info().rss
        traced_current, traced_peak = (
            tracemalloc.get_traced_memory() if tracemalloc.is_tracing() else (0, 0)
        )

        top: list[str] = []
        if tracemalloc.is_tracing():
            snapshot = tracemalloc.take_snapshot()
            for stat in snapshot.statistics("lineno")[:TOP_ALLOCATIONS]:
                frame = stat.traceback[0]
                top.append(
                    f"{frame.filename.split('/')[-1]}:{frame.lineno} "
                    f"{stat.size / 1024:.0f} KB ({stat.count} bloques)"
                )

        sample = {
            "t": time.monotonic(),
            "rss_mb": round(rss / 1e6, 1),
            "rss_delta_mb": round((rss - (self._baseline_rss or rss)) / 1e6, 1),
            "traced_mb": round(traced_current / 1e6, 1),
            "traced_peak_mb": round(traced_peak / 1e6, 1),
            "top_allocations": top,
        }
        self.samples.append(sample)
        logger.info(
            "Perf: RSS %.1f MB (%+.1f MB), Python %.1f MB | %s",
            sample["rss_mb"], sample["rss_delta_mb"], sample["traced_mb"],
            "; ".join(top[:2]) or "sin datos",
        )
        return sample

    def summary(self) -> dict:
        """Resumen de la sesion: crecimiento total y maximo observado."""
        if not self.samples:
            return {"samples": 0}
        rss_values = [s["rss_mb"] for s in self.samples]
        return {
            "samples": len(self.samples),
            "rss_first_mb": rss_values[0],
            "rss_last_mb": rss_values[-1],
            "rss_max_mb": max(rss_values),
            "growth_mb": round(rss_values[-1] - rss_values[0], 1),
        }
