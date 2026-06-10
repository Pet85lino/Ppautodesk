"""
database/database.py
--------------------
Capa de persistencia SQLite de la suite.

Tablas:
    devices          -> catalogo de dispositivos vistos (mac unica)
    scan_history     -> cada deteccion en un escaneo (rssi + timestamp)
    battery_history  -> lecturas de bateria para analisis de degradacion
    power_history    -> historial energetico del host (carga/descarga)
    latency_history  -> mediciones de latencia/jitter de audio
    logs             -> eventos tecnicos persistentes

Exportacion: dump completo en JSON o CSV (un archivo por tabla) hacia
la carpeta exports/ del proyecto.

Nota de hilos: todas las escrituras llegan desde el hilo principal de Qt
(las senales del motor BLE se entregan ahi), por lo que una unica conexion
con `check_same_thread=False` y commits inmediatos es suficiente.
"""

from __future__ import annotations

import csv
import json
import logging
import sqlite3
from datetime import datetime
from pathlib import Path

logger = logging.getLogger("lino.database")

EXPORT_TABLES = (
    "devices",
    "scan_history",
    "battery_history",
    "power_history",
    "latency_history",
    "device_capabilities",
    "ble_events",
    "adv_timeline",
    "charge_history",
    "logs",
)

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

CREATE TABLE IF NOT EXISTS power_history (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    percent   REAL,
    plugged   INTEGER,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS latency_history (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    latency_ms     REAL NOT NULL,
    jitter_ms      REAL,
    confidence     REAL,
    channel        TEXT,
    classification TEXT,
    timestamp      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS device_capabilities (
    mac               TEXT PRIMARY KEY,
    name              TEXT,
    manufacturer_data TEXT,   -- JSON {company_id_hex: payload_hex}
    uuids             TEXT,   -- JSON lista de UUIDs anunciados
    services          TEXT,   -- JSON inventario GATT completo
    mtu               INTEGER,
    avg_rssi          REAL,
    codecs            TEXT,   -- JSON codecs probables
    updated_at        TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ble_events (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    mac       TEXT,
    event_type TEXT NOT NULL,  -- disconnected | connect_failed | rssi_jump | out_of_range
    detail    TEXT,
    timestamp TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS adv_timeline (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    mac        TEXT NOT NULL,
    t_ms       REAL,        -- offset dentro de la captura
    rssi       INTEGER,
    payload    TEXT,        -- manufacturer data en hex (JSON)
    capture_ts TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS charge_history (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    source       TEXT,        -- medidor de origen (UM25C, FNB58, ...)
    voltage_v    REAL,
    current_a    REAL,
    power_w      REAL,
    capacity_mah REAL,
    temp_c       REAL,
    timestamp    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS logs (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    level     TEXT NOT NULL,
    message   TEXT NOT NULL,
    timestamp TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_scan_mac ON scan_history (mac);
CREATE INDEX IF NOT EXISTS idx_battery_mac ON battery_history (mac);
CREATE INDEX IF NOT EXISTS idx_ble_events_mac ON ble_events (mac);
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

    def save_power(self, percent: float | None, plugged: bool | None) -> None:
        """Registra el estado energetico del host (sesiones carga/descarga)."""
        if percent is None:
            return
        try:
            with self._conn:
                self._conn.execute(
                    "INSERT INTO power_history (percent, plugged, timestamp) VALUES (?, ?, ?)",
                    (percent, 1 if plugged else 0, _now()),
                )
        except sqlite3.Error as exc:
            logger.error("Error guardando energia: %s", exc)

    def save_latency(
        self,
        latency_ms: float,
        jitter_ms: float | None = None,
        confidence: float | None = None,
        channel: str = "both",
        classification: str = "",
    ) -> None:
        """Registra una medicion de latencia/jitter de audio."""
        try:
            with self._conn:
                self._conn.execute(
                    """INSERT INTO latency_history
                       (latency_ms, jitter_ms, confidence, channel, classification, timestamp)
                       VALUES (?, ?, ?, ?, ?, ?)""",
                    (latency_ms, jitter_ms, confidence, channel, classification, _now()),
                )
        except sqlite3.Error as exc:
            logger.error("Error guardando latencia: %s", exc)

    def save_fingerprint(self, fingerprint: dict) -> None:
        """Persiste/actualiza las capacidades GATT de un dispositivo."""
        try:
            with self._conn:
                self._conn.execute(
                    """INSERT INTO device_capabilities
                       (mac, name, manufacturer_data, uuids, services, mtu,
                        avg_rssi, codecs, updated_at)
                       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                       ON CONFLICT(mac) DO UPDATE SET
                           name = excluded.name,
                           manufacturer_data = excluded.manufacturer_data,
                           uuids = excluded.uuids,
                           services = excluded.services,
                           mtu = excluded.mtu,
                           avg_rssi = excluded.avg_rssi,
                           codecs = excluded.codecs,
                           updated_at = excluded.updated_at""",
                    (
                        fingerprint["mac"],
                        fingerprint.get("name"),
                        json.dumps(fingerprint.get("manufacturer_data", {})),
                        json.dumps(fingerprint.get("uuids", [])),
                        json.dumps(fingerprint.get("services", [])),
                        fingerprint.get("mtu"),
                        fingerprint.get("avg_rssi"),
                        json.dumps(fingerprint.get("codecs", [])),
                        _now(),
                    ),
                )
        except sqlite3.Error as exc:
            logger.error("Error guardando fingerprint: %s", exc)

    def get_fingerprint(self, mac: str) -> dict | None:
        """Capacidades guardadas de un dispositivo (JSON deserializado)."""
        try:
            row = self._conn.execute(
                "SELECT * FROM device_capabilities WHERE mac = ?", (mac,)
            ).fetchone()
            if row is None:
                return None
            columns = [d[0] for d in self._conn.execute(
                "SELECT * FROM device_capabilities LIMIT 0"
            ).description]
            data = dict(zip(columns, row))
            for key in ("manufacturer_data", "uuids", "services", "codecs"):
                if data.get(key):
                    data[key] = json.loads(data[key])
            return data
        except (sqlite3.Error, json.JSONDecodeError) as exc:
            logger.error("Error leyendo fingerprint: %s", exc)
            return None

    def save_ble_event(self, mac: str, event_type: str, detail: str) -> None:
        """Logging BLE: desconexiones, saltos RSSI, dispositivos perdidos."""
        try:
            with self._conn:
                self._conn.execute(
                    "INSERT INTO ble_events (mac, event_type, detail, timestamp) VALUES (?, ?, ?, ?)",
                    (mac, event_type, detail, _now()),
                )
        except sqlite3.Error as exc:
            logger.error("Error guardando evento BLE: %s", exc)

    def save_raw_log(self, raw_log: dict) -> int:
        """Persiste una captura cruda de advertising (adv_timeline).

        Returns:
            Numero de muestras guardadas.
        """
        samples = raw_log.get("samples", [])
        ts = _now()
        try:
            with self._conn:
                self._conn.executemany(
                    """INSERT INTO adv_timeline (mac, t_ms, rssi, payload, capture_ts)
                       VALUES (?, ?, ?, ?, ?)""",
                    [
                        (
                            s["mac"],
                            s.get("t_ms"),
                            s.get("rssi"),
                            json.dumps(s.get("manufacturer_data", {})),
                            ts,
                        )
                        for s in samples
                    ],
                )
            return len(samples)
        except sqlite3.Error as exc:
            logger.error("Error guardando raw log: %s", exc)
            return 0

    def save_charge_sample(
        self,
        source: str,
        voltage_v: float | None,
        current_a: float | None,
        power_w: float | None,
        capacity_mah: float | None = None,
        temp_c: float | None = None,
    ) -> None:
        """Muestra de un medidor USB (curvas de carga)."""
        try:
            with self._conn:
                self._conn.execute(
                    """INSERT INTO charge_history
                       (source, voltage_v, current_a, power_w, capacity_mah, temp_c, timestamp)
                       VALUES (?, ?, ?, ?, ?, ?, ?)""",
                    (source, voltage_v, current_a, power_w, capacity_mah, temp_c, _now()),
                )
        except sqlite3.Error as exc:
            logger.error("Error guardando muestra de carga: %s", exc)

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

    def _dump_table(self, table: str) -> tuple[list[str], list[tuple]]:
        """Columnas y filas completas de una tabla (solo tablas conocidas)."""
        if table not in EXPORT_TABLES:
            raise ValueError(f"Tabla no exportable: {table}")
        cursor = self._conn.execute(f"SELECT * FROM {table}")  # noqa: S608
        columns = [desc[0] for desc in cursor.description]
        return columns, cursor.fetchall()

    # ------------------------------------------------------------------
    # Exportacion JSON / CSV
    # ------------------------------------------------------------------
    def export_json(self, out_dir: Path) -> Path | None:
        """Exporta todas las tablas a un unico archivo JSON."""
        try:
            out_dir.mkdir(parents=True, exist_ok=True)
            payload: dict = {"exported_at": _now(), "tables": {}}
            for table in EXPORT_TABLES:
                columns, rows = self._dump_table(table)
                payload["tables"][table] = [dict(zip(columns, row)) for row in rows]

            out_path = out_dir / f"lino_export_{datetime.now():%Y%m%d_%H%M%S}.json"
            with open(out_path, "w", encoding="utf-8") as fh:
                json.dump(payload, fh, ensure_ascii=False, indent=2)
            logger.info("Exportacion JSON: %s", out_path)
            return out_path
        except (sqlite3.Error, OSError) as exc:
            logger.error("Error exportando JSON: %s", exc)
            return None

    def export_csv(self, out_dir: Path) -> list[Path]:
        """Exporta cada tabla a su propio CSV (lino_<tabla>_<ts>.csv)."""
        paths: list[Path] = []
        stamp = f"{datetime.now():%Y%m%d_%H%M%S}"
        try:
            out_dir.mkdir(parents=True, exist_ok=True)
            for table in EXPORT_TABLES:
                columns, rows = self._dump_table(table)
                out_path = out_dir / f"lino_{table}_{stamp}.csv"
                with open(out_path, "w", encoding="utf-8", newline="") as fh:
                    writer = csv.writer(fh)
                    writer.writerow(columns)
                    writer.writerows(rows)
                paths.append(out_path)
            logger.info("Exportacion CSV: %d archivo(s) en %s", len(paths), out_dir)
        except (sqlite3.Error, OSError) as exc:
            logger.error("Error exportando CSV: %s", exc)
        return paths

    def close(self) -> None:
        """Cierra la conexion de forma ordenada al salir de la app."""
        try:
            self._conn.close()
        except sqlite3.Error:
            pass
