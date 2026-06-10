# LINO Audio Diagnostic Suite

Suite tecnica multiplataforma de diagnostico para auriculares Bluetooth TWS
(True Wireless Stereo): Maxell, JBL, Xiaomi, Samsung, Sony y genericos.

## Version actual: V1.0 (MVP)

- Escaneo Bluetooth BLE (`bleak`) con auto-refresh cada 5 segundos
- Nombre, MAC, RSSI y fabricante (Company ID) de cada dispositivo
- Lectura de bateria via GATT Battery Service (0x180F / 0x2A19)
- Dashboard PySide6 con tema oscuro glassmorphism y panel lateral
- Historial persistente en SQLite (dispositivos, RSSI, bateria, logs)
- Consola de logs en vivo + archivo rotativo `logs/lino.log`
- Tests de audio basicos: tono L/R, balance y latencia por loopback
- Estado de energia del equipo (psutil)

## Requisitos

- Python 3.12+
- Adaptador Bluetooth con soporte BLE
- Windows 11 (prioritario), Linux y macOS soportados por `bleak`

## Instalacion y uso

```bash
pip install -r requirements.txt
python main.py
```

## Estructura

```
LINO_AUDIO_DIAGNOSTIC/
├── main.py              # punto de entrada
├── config.json          # configuracion (intervalos, BD, UI)
├── core/                # app_manager, config, logger
├── ble/                 # motor BLE, lecturas GATT, parser
├── audio/               # tests de tono, balance y latencia
├── usb/                 # monitoreo energetico
├── database/            # persistencia SQLite
├── ui/                  # dashboard, temas, widgets
├── logs/                # logs rotativos
└── tests/               # pruebas unitarias
```

## Tests

```bash
python -m unittest discover tests
```

## Limitaciones conocidas

- No todos los TWS exponen el Battery Service GATT estandar; muchos usan
  protocolos propietarios (AirPods, algunos Sony/JBL) y mostraran "N/D".
- La bateria del case rara vez es accesible via BLE estandar.
- La latencia medida por loopback incluye el stack de audio del SO
  (valor comparativo, no de laboratorio).

## Hoja de ruta

- **V2.0**: analisis de audio avanzado, exportaciones CSV/JSON, logs
  avanzados, deteccion de carga USB del case.
- **V3.0**: reverse engineering GATT, sniffing BLE, herramientas de
  firmware, estimacion de degradacion de bateria.
