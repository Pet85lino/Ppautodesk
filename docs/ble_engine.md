# Motor BLE

`ble/ble_scanner.py` — BLEEngine (QThread con event loop asyncio).

## Por que un hilo dedicado

`bleak` es asyncio; Qt tiene su propio event loop. El motor corre
`asyncio.run_forever()` dentro de un QThread y recibe trabajo via
`asyncio.run_coroutine_threadsafe`. La UI llama `request_*()` y recibe
los resultados por senales Qt (thread-safe).

## API publica

| Metodo                      | Senal de resultado                  |
|-----------------------------|-------------------------------------|
| `request_scan()`            | `scan_finished(list[DeviceInfo])`   |
| `request_battery(mac)`      | `battery_read(mac, level|None)`     |
| `request_fingerprint(mac)`  | `fingerprint_ready(mac, dict)`      |
| `request_raw_log(duration)` | `raw_log_ready(dict)`               |

Senales adicionales: `device_connected`, `ble_event(mac, tipo, detalle)`,
`engine_error(str)`.

## Estrategia de timeouts (Windows)

* **Backoff exponencial**: `_open_client()` reintenta la conexion con
  esperas 1 s, 2 s, 4 s... (config: `ble.retry_attempts`,
  `ble.backoff_base_s`). Cada reintento emite `connect_retry`.
* **Watchdog del adaptador**: 3 escaneos fallidos consecutivos emiten
  `adapter_watchdog`; el dashboard duplica el intervalo de auto-refresh
  (hasta 60 s). El primer escaneo exitoso emite `adapter_recovered` y
  restaura el intervalo configurado.
* **Desconexion tolerante**: `_safe_disconnect()` nunca propaga errores
  de cierre (frecuentes en stacks Windows).

## Eventos BLE persistidos (tabla ble_events)

| Tipo              | Origen                                          |
|-------------------|-------------------------------------------------|
| `disconnected`    | callback de bleak (desconexion inesperada)      |
| `connect_failed`  | agotados los reintentos                         |
| `connect_retry`   | un intento intermedio fallo                     |
| `rssi_jump`       | delta >= 15 dB entre escaneos consecutivos      |
| `out_of_range`    | visto en el escaneo anterior, ausente ahora     |
| `adapter_watchdog`/`adapter_recovered` | salud del adaptador local |

## Raw logger (adv_timeline)

`request_raw_log()` usa `BleakScanner(detection_callback=...)`: ve CADA
advertisement (no un snapshot), lo que permite medir intervalos de
advertising reales y detectar anomalias de emision por chipset. Las
muestras van a la tabla `adv_timeline` (batch `executemany`).
