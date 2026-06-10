"""
usb/charge_recorder.py
----------------------
Grabador de curvas de carga reales con medidores USB (UM25C, etc.).

Funcionamiento:
    * Un QThread sondea el medidor cada `interval_s` segundos.
    * Cada ChargeSample se emite por senal (la persistencia a la tabla
      charge_history ocurre en el hilo principal) y se vigila la
      temperatura (alerta de sobrecalentamiento).
    * plot_charge_curve() grafica V/A/W/temperatura contra el tiempo a
      partir del historico SQLite -> deteccion visual de baterias
      danadas (corriente inestable, carga que no progresa, calor).

El lector es inyectable (`reader`), lo que permite probar el grabador
sin hardware y conectar protocolos nuevos (FNB58/TC66C) en V2.
"""

from __future__ import annotations

import logging
import time
from datetime import datetime
from pathlib import Path
from typing import Callable

from PySide6.QtCore import QThread, Signal

from usb.usb_meter import ChargeSample, read_meter

logger = logging.getLogger("lino.usb.charge")

OVERHEAT_TEMP_C = 45.0


class ChargeRecorder(QThread):
    """Sondea un medidor USB y emite muestras hasta que se detenga."""

    sample_ready = Signal(object)   # ChargeSample
    overheat = Signal(float)        # temperatura en C
    recorder_error = Signal(str)

    def __init__(
        self,
        port: str,
        meter_model: str,
        interval_s: float = 2.0,
        reader: Callable[[str, str], ChargeSample | None] = read_meter,
    ):
        super().__init__()
        self._port = port
        self._model = meter_model
        self._interval = max(0.5, interval_s)
        self._reader = reader
        self._running = False

    def run(self) -> None:
        self._running = True
        failures = 0
        logger.info(
            "Grabando curva de carga: %s en %s cada %.1f s",
            self._model, self._port, self._interval,
        )
        while self._running:
            sample = self._reader(self._port, self._model)
            if sample is None:
                failures += 1
                if failures >= 3:
                    self.recorder_error.emit(
                        f"El medidor {self._model} no responde en {self._port}"
                    )
                    break
            else:
                failures = 0
                self.sample_ready.emit(sample)
                if sample.temp_c is not None and sample.temp_c >= OVERHEAT_TEMP_C:
                    logger.warning("ALERTA: sobrecalentamiento %.0f C", sample.temp_c)
                    self.overheat.emit(sample.temp_c)
            # Espera fraccionada: permite detener sin bloquear el cierre.
            deadline = time.monotonic() + self._interval
            while self._running and time.monotonic() < deadline:
                time.sleep(0.1)
        self._running = False
        logger.info("Grabacion de curva de carga detenida")

    def stop(self) -> None:
        self._running = False
        self.wait(3000)


def plot_charge_curve(db, source: str, out_dir: Path, limit: int = 2000) -> Path | None:
    """Grafica la curva de carga registrada (V, A, W, temperatura).

    Returns:
        Ruta del PNG o None (sin datos o sin matplotlib).
    """
    try:
        rows = db._conn.execute(
            """SELECT voltage_v, current_a, power_w, temp_c, timestamp
               FROM charge_history WHERE source = ?
               ORDER BY id DESC LIMIT ?""",
            (source, limit),
        ).fetchall()[::-1]
    except Exception as exc:  # noqa: BLE001
        logger.error("Error leyendo charge_history: %s", exc)
        return None
    if len(rows) < 2:
        logger.info("Curva de carga %s: sin datos suficientes", source)
        return None

    try:
        import matplotlib

        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except ImportError:
        logger.warning("matplotlib no disponible: sin curva de carga")
        return None

    voltage = [r[0] for r in rows]
    current = [r[1] for r in rows]
    power = [r[2] for r in rows]
    temp = [r[3] for r in rows]

    fig, axes = plt.subplots(3, 1, figsize=(10, 8), facecolor="#0A1428", sharex=True)
    series = [
        (axes[0], voltage, "Voltaje (V)", "#00E5FF"),
        (axes[1], current, "Corriente (A)", "#2EE6A8"),
        (axes[2], power, "Potencia (W)", "#FF2E97"),
    ]
    for ax, values, title, color in series:
        ax.set_facecolor("#081020")
        ax.tick_params(colors="#7A8BA3", labelsize=8)
        for spine in ax.spines.values():
            spine.set_color("#1E2A44")
        ax.plot(values, color=color, linewidth=1.0)
        ax.set_title(title, color="#E6F1FF", fontsize=10)

    # Temperatura superpuesta en el eje de potencia (si el medidor la da).
    if any(t is not None for t in temp):
        twin = axes[2].twinx()
        twin.plot([t if t is not None else float("nan") for t in temp],
                  color="#FFC94D", linewidth=0.8, linestyle="--")
        twin.set_ylabel("Temp (C)", color="#FFC94D", fontsize=8)
        twin.tick_params(colors="#7A8BA3", labelsize=7)

    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / f"charge_curve_{source}_{datetime.now():%Y%m%d_%H%M%S}.png"
    fig.tight_layout()
    fig.savefig(out_path, dpi=110)
    plt.close(fig)
    logger.info("Curva de carga guardada: %s", out_path)
    return out_path
