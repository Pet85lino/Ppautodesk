#!/usr/bin/env python3
"""
tools/soak_test.py
------------------
Soak test headless: validacion empirica de sesiones largas (overnight).

Ejecuta ciclos continuos de escaneo BLE sin UI, registrando todo en la
base de datos y muestreando memoria/rendimiento. Es la herramienta para
las matrices de validacion real (adaptadores Windows x TWS):

    python tools/soak_test.py --hours 8 --interval 5
    python tools/soak_test.py --minutes 10 --battery AA:BB:CC:DD:EE:FF

Al terminar imprime un resumen: escaneos, fallos, eventos BLE, watchdog
y crecimiento de memoria. Los detalles quedan en logs/ y en SQLite.
"""

from __future__ import annotations

import argparse
import signal
import sys
import time
from pathlib import Path

# Ejecutable desde la raiz o desde tools/.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from PySide6.QtCore import QCoreApplication, QTimer  # noqa: E402

from core.app_manager import AppManager  # noqa: E402
from core.perf_monitor import PerfMonitor  # noqa: E402


def main() -> int:
    parser = argparse.ArgumentParser(description="Soak test BLE headless")
    parser.add_argument("--hours", type=float, default=0.0)
    parser.add_argument("--minutes", type=float, default=0.0)
    parser.add_argument("--interval", type=float, default=5.0,
                        help="segundos entre escaneos")
    parser.add_argument("--battery", type=str, default=None,
                        help="MAC a la que leer bateria cada 10 ciclos")
    args = parser.parse_args()

    duration_s = args.hours * 3600 + args.minutes * 60
    if duration_s <= 0:
        duration_s = 600  # 10 minutos por defecto
    print(f"Soak test: {duration_s / 60:.0f} min, escaneo cada {args.interval:.0f} s")

    app = QCoreApplication(sys.argv)
    manager = AppManager()
    manager.start()

    perf = PerfMonitor(interval_s=60.0)
    perf.start()

    stats = {"scans": 0, "devices_seen": set(), "errors": 0, "events": 0}
    t_end = time.monotonic() + duration_s

    def on_scan(devices: list) -> None:
        stats["scans"] += 1
        for dev in devices:
            stats["devices_seen"].add(dev.mac)
        if args.battery and stats["scans"] % 10 == 0:
            manager.ble_engine.request_battery(args.battery)

    def on_error(_msg: str) -> None:
        stats["errors"] += 1

    def on_event(_mac: str, _type: str, _detail: str) -> None:
        stats["events"] += 1

    manager.ble_engine.scan_finished.connect(on_scan)
    manager.ble_engine.engine_error.connect(on_error)
    manager.ble_engine.ble_event.connect(on_event)

    def tick() -> None:
        if time.monotonic() >= t_end:
            finish()
            return
        manager.ble_engine.request_scan()

    def finish() -> None:
        timer.stop()
        perf_summary = perf.summary()
        perf.stop()
        manager.shutdown()
        print("\n===== RESUMEN SOAK TEST =====")
        print(f"Escaneos completados : {stats['scans']}")
        print(f"Dispositivos unicos  : {len(stats['devices_seen'])}")
        print(f"Errores de escaneo   : {stats['errors']}")
        print(f"Eventos BLE          : {stats['events']}")
        print(f"Memoria              : {perf_summary}")
        app.quit()

    timer = QTimer()
    timer.setInterval(int(args.interval * 1000))
    timer.timeout.connect(tick)
    timer.start()

    # Ctrl+C termina con resumen en lugar de morir a medias.
    signal.signal(signal.SIGINT, lambda *_: finish())

    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
