# Notas Windows + protocolo de validacion empirica

La teoria, la arquitectura y los tests sinteticos estan cubiertos por
CI. Lo que sigue es validacion con hardware real: este documento es el
protocolo para hacerla de forma sistematica.

## Matriz de adaptadores BLE (prioridad 1)

Windows BLE (WinRT) se comporta distinto segun el chipset del
adaptador. Probar al menos:

| Adaptador        | Que vigilar                                       |
|------------------|---------------------------------------------------|
| Intel AX200/210  | referencia moderna; suele ser el mas estable      |
| Realtek (RTL8761)| timeouts de conexion intermitentes                |
| CSR dongles      | drivers antiguos, discover() lento o vacio        |
| Qualcomm         | coexistencia WiFi/BT (saltos RSSI)                |
| Broadcom         | callbacks de desconexion poco fiables             |

Por cada adaptador, correr:

```
python tools/soak_test.py --minutes 30 --interval 5
```

y registrar: escaneos ok/fallidos, eventos `adapter_watchdog`, eventos
`connect_retry`/`connect_failed` (quedan en SQLite -> exportar JSON).

## Matriz de TWS (prioridad 2)

| TWS        | Comportamiento esperado                                |
|------------|--------------------------------------------------------|
| Genericos  | suelen exponer Battery Service estandar (0x180F)       |
| Xiaomi     | bateria BLE basica; UUIDs propietarios frecuentes      |
| JBL        | datos limitados; protocolo Harman propietario          |
| Samsung    | UUID 0xFD5A; bateria via protocolo propietario         |
| Sony       | Battery Service raro; telemetria propietaria           |
| AirPods    | NO conectables via GATT generico; solo advertisement   |

Checklist por dispositivo: deteccion en escaneo -> fingerprint ->
lectura de bateria -> diagnostico completo -> revisar sesion generada.

## Sesiones largas (prioridad 3)

* Activar `perf.monitor_enabled: true` en config.json (muestrea RSS +
  tracemalloc cada 60 s al log).
* Soak overnight: `python tools/soak_test.py --hours 8`.
* Revisar al terminar: crecimiento de memoria (`growth_mb`), tamano de
  `lino.log` (rotacion a 1 MB x3), conteos en `ble_events`.

## Problemas conocidos de Windows BLE

* El primer `discover()` tras encender Bluetooth puede devolver vacio.
* `connect()` puede tardar >10 s con el dispositivo dentro del case.
* Las MAC pueden ser aleatorizadas (privacy) en algunos TWS: el
  historial por MAC se fragmenta — limitacion conocida.
* El RSSI de WinRT se actualiza por advertisement, no en conexion.
* Audio compartido: si otro proceso usa el dispositivo en modo
  exclusivo, `sounddevice` falla con PortAudioError (capturado).

## Donde mirar cuando algo falla

1. `logs/lino.log` (DEBUG via config `logging.level`).
2. Tabla `ble_events` (exportar con el boton JSON).
3. `sessions/<ts>_<mac>/ble_log.json` del diagnostico afectado.
4. Resumen del soak test (stdout).
