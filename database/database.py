"""
database/database.py
--------------------
Capa de persistencia SQLite de la suite.

Tablas:
    devices          -> catalogo de dispositivos vistos (mac unica)
    scan_history     -> cada deteccion en un escaneo (rssi + timestamp)
    battery_history  -> lecturas de bateria para analisis de degradacion
    logs             -> eventos tecnicos persistentes

Nota de hilos: todas las escrituras llegan desde el hilo principal de Qt
(las senales del motor BLE se entregan ahi), por lo que una unica conexion
con `check_same_thread=False` y commits inmediatos es suficiente en el MVP.
"""

from __future__ import annotations

import logging
import sqlite3
from datetime import datetime
from pathlib import Path

logger = logging.getLogger("lino.database")

SCHEMA = """
CREATE TABLE IF NOT EXISTS devices (
    mac         TEXT PRIMARY KEY,
    name        TEXT,
    manufacturer TEXT,
    first_seen  TEXT NOT NULL,
    last_seen   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS scan_history (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    mac       TEXT NOT NULL,
    name      TEXT,
    rssi      INTEGER,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS battery_history (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    mac       TEXT NOT NULL,
    level     INTEGER,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS logs (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    level     TEXT NOT NULL,
    message   TEXT NOT NULL,
    timestamp TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_scan_mac ON scan_history (mac);
CREATE INDEX IF NOT EXISTS idx_battery_mac ON battery_history (mac);
"""


def _now() -> str:
    return datetime.now().isoformat(timespec="seconds")


class DatabaseManager:
    """Gestor unico de la base de datos SQLite."""

    def __init__(self, db_path: Path):
        db_path.parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(str(db_path), check_same_thread=False)
        self._conn.executescript(SCHEMA)
        self._conn.commit()
        logger.info("Base de datos lista en %s", db_path)

    # ------------------------------------------------------------------
    # Escrituras
    # ------------------------------------------------------------------
    def save_scan_results(self, devices) -> None:
        """Registra un escaneo: actualiza catalogo y agrega historial RSSI.

        Args:
            devices: iterable de DeviceInfo (ble.ble_scanner).
        """
        ts = _now()
        try:
            with self._conn:
                for dev in devices:
                    self._conn.execute(
                        """INSERT INTO devices (mac, name, manufacturer, first_seen, last_seen)
                           VALUES (?, ?, ?, ?, ?)
                           ON CONFLICT(mac) DO UPDATE SET
                               name = excluded.name,
                               manufacturer = excluded.manufacturer,
                               last_seen = excluded.last_seen""",
                        (dev.mac, dev.name, dev.manufacturer, ts, ts),
                    )
                    self._conn.execute(
                        "INSERT INTO scan_history (mac, name, rssi, timestamp) VALUES (?, ?, ?, ?)",
                        (dev.mac, dev.name, dev.rssi, ts),
                    )
        except sqlite3.Error as exc:
            logger.error("Error guardando escaneo: %s", exc)

    def save_battery(self, mac: str, level: int | None) -> None:
        """Registra una lectura de bateria (base del analisis de degradacion)."""
        if level is None:
            return
        try:
            with self._conn:
                self._conn.execute(
                    "INSERT INTO battery_history (mac, level, timestamp) VALUES (?, ?, ?)",
                    (mac, level, _now()),
                )
        except sqlite3.Error as exc:
            logger.error("Error guardando bateria: %s", exc)

    def save_log(self, level: str, message: str) -> None:
        """save_log(): persiste un evento tecnico en la tabla logs."""
        try:
            with self._conn:
                self._conn.execute(
                    "INSERT INTO logs (level, message, timestamp) VALUES (?, ?, ?)",
                    (level, message, _now()),
                )
        except sqlite3.Error as exc:
            logger.error("Error guardando log: %s", exc)

    # ------------------------------------------------------------------
    # Lecturas
    # ------------------------------------------------------------------
    def battery_history(self, mac: str, limit: int = 50) -> list[tuple[str, int]]:
        """Ultimas lecturas de bateria de un dispositivo (timestamp, nivel)."""
        try:
            rows = self._conn.execute(
                """SELECT timestamp, level FROM battery_history
                   WHERE mac = ? ORDER BY id DESC LIMIT ?""",
                (mac, limit),
            ).fetchall()
            return list(reversed(rows))
        except sqlite3.Error as exc:
            logger.error("Error leyendo historial de bateria: %s", exc)
            return []

    def known_devices_count(self) -> int:
        """Total de dispositivos distintos vistos historicamente."""
        try:
            row = self._conn.execute("SELECT COUNT(*) FROM devices").fetchone()
            return int(row[0]) if row else 0
        except sqlite3.Error:
            return 0

    def close(self) -> None:
        """Cierra la conexion de forma ordenada al salir de la app."""
        try:
            self._conn.close()
        except sqlite3.Error:
            pass
