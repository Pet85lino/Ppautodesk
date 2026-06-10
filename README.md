# LINO Audio Diagnostic Suite

Suite tecnica multiplataforma de diagnostico para auriculares Bluetooth TWS
(True Wireless Stereo): Maxell, JBL, Xiaomi, Samsung, Sony y genericos.

## Version actual: V1.1 (0.2.0)

### Bluetooth BLE
- Escaneo BLE (`bleak`) con auto-refresh cada 5 segundos
- Nombre, MAC, RSSI y fabricante (Company ID) de cada dispositivo
- Lectura de bateria via GATT Battery Service (0x180F / 0x2A19)

### Audio
- Seleccion explicita del dispositivo de salida (solo outputs listados)
- Tono L/R, balance, ruido blanco, ruido rosa y sweep 20 Hz - 20 kHz
- Medicion objetiva RMS L/R por loopback (drivers deteriorados)
- Proteccion auditiva: volumen y duracion con limites duros
- Compatibilidad async (`run_async`) para correr en paralelo con BLE

### Latencia (DSP)
- Chirp logaritmico `scipy.signal.chirp` con ventana Hann
- Correlacion cruzada FFT con normalizacion de senales
- Confidence score (peak-to-sidelobe ratio) por medicion
- Jitter test (N corridas: media + desviacion estandar)
- Latencia por canal y test de sincronizacion stereo (drift TWS)
- Clasificacion automatica: Excelente / Buena / Normal / Elevada / Alta
- Grafica de diagnostico (waveform + correlacion) en PNG

### Energia / USB
- Estado de energia tipado (dataclass `PowerStatus`)
- Historial energetico en SQLite (sesiones de carga/descarga)
- Deteccion dinamica de puertos serie y medidores USB conocidos
  (UM25C, FNB58, TC66C, AT34) via pyserial
- Sistema de alertas: bateria critica, descarga anormal, transiciones

### Plataforma
- Dashboard PySide6 con tema oscuro glassmorphism y panel lateral
- Historial persistente en SQLite (6 tablas)
- Exportacion JSON (archivo unico) y CSV (uno por tabla) a `exports/`
- Consola de logs en vivo + archivo rotativo `logs/lino.log`

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

- **V2.0**: protocolos de lectura de medidores USB (voltaje/corriente/mAh
  reales), curvas de carga graficadas, soporte multi-dispositivo
  simultaneo, espectrograma en la UI.
- **V3.0**: reverse engineering GATT, sniffing BLE, herramientas de
  firmware, estimacion de degradacion de bateria con historico.
