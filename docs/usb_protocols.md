# Protocolos de medidores USB

`usb/usb_meter.py` + `usb/charge_recorder.py`.

## Estado por medidor

| Medidor | Transporte | Estado                                        |
|---------|------------|-----------------------------------------------|
| UM24C   | Serie/BT SPP | Implementado (divisores /100, /1000) — EXPERIMENTAL |
| UM25C   | Serie/BT SPP | Implementado (divisores /1000, /10000) — EXPERIMENTAL |
| TC66C   | Serie/BLE  | Pendiente V2: payload cifrado AES-ECB          |
| FNB58   | USB HID    | Pendiente V2: requiere hidapi                  |
| AT34    | BLE        | Pendiente V2                                   |

"EXPERIMENTAL" = implementado contra la documentacion publica del
protocolo, pendiente de validar con hardware real.

## Protocolo UM24C/UM25C

* 9600 baudios, 8N1.
* Peticion: byte `0xF0` -> respuesta de **130 bytes**.
* Offsets usados (big-endian):

| Offset | Campo        | Conversion UM25C        |
|--------|--------------|--------------------------|
| 2-3    | voltaje      | /1000 -> V               |
| 4-5    | corriente    | /10000 -> A              |
| 6-9    | potencia     | /1000 -> W               |
| 10-11  | temperatura  | valor directo en C       |
| 16-19  | capacidad    | mAh acumulados           |

El UM24C usa divisores /100 (V) y /1000 (A).

## ChargeRecorder

QThread que sondea `reader(port, model)` cada `interval_s`:

* `sample_ready(ChargeSample)` por muestra valida.
* `overheat(temp)` si temp >= 45 C.
* `recorder_error(str)` tras 3 lecturas fallidas consecutivas (y para).
* `stop()` detiene con espera fraccionada (cierre limpio < 3 s).

El `reader` es **inyectable**: los tests usan lectores falsos y los
protocolos nuevos (TC66C/FNB58) solo implementan esa funcion.

## Persistencia

Tabla `charge_history` (source, V, A, W, mAh, temp, ts).
`plot_charge_curve(db, source, out_dir)` -> PNG con V/A/W y temperatura
superpuesta. `analytics.temperature_analytics` resume el comportamiento
termico.

## Deteccion

`usb_monitor.list_serial_ports()` identifica medidores por palabras
clave en descripcion/HWID (UM25C, FNB58, TC66, CP210x, CH340).
