# LINO Audio Diagnostic Suite

Suite tecnica multiplataforma de diagnostico para auriculares Bluetooth TWS
(True Wireless Stereo): Maxell, JBL, Xiaomi, Samsung, Sony y genericos.

## Version actual: V1.5 (0.6.0)

### Novedades V1.5 (Microphone Profiler)
- **Perfil de microfono uplink** (`audio/mic_profile.py`):
  - canales de entrada expuestos -> topologia (mono/dual/array), con la
    advertencia honesta de que 1 canal expuesto != 1 mic fisico
  - deteccion ENC por convergencia del piso de ruido (>= 6 dB = Detected)
  - sensibilidad aparente (dBFS) y claridad de voz (% energia 300-3400 Hz)
  - hardware estimado cruzando con el chipset del fingerprint BLE
    (Qualcomm dual ENC, Airoha dual, Realtek mono, Apple array...)
  - confianza explicita (max 0.85: sin teardown no hay certeza)
- Selector de microfono en la pagina Audio + boton "Perfil de mic"
  que genera el reporte `=== MICROPHONE PROFILE ===`

---

### Historial V1.4 (0.5.0)

### Novedades V1.4 (robustez y validacion)
- **Estrategia de timeouts BLE**: reconexion con backoff exponencial
  (1-2-4 s, configurable) y watchdog del adaptador (3 fallos -> alerta
  `adapter_watchdog` + auto-refresh escalado; se restaura al recuperar)
- **Thread-safety**: SQLite en modo WAL + lock de proceso; verificado
  con 8 hilos escribiendo concurrentemente sin perdida de filas
- **Preparacion DuckDB**: toda consulta pasa por la API de
  `DatabaseManager`; ningun consumidor toca SQL directo
- **Monitor de memoria/rendimiento**: tracemalloc + RSS muestreados
  periodicamente (config `perf.monitor_enabled`) para sesiones overnight
- **Versionado de reportes**: cada reporte/sesion incluye
  `schema_version`, `analysis_engine`, `scoring_version`, `analytics_version`
- **Base de patrones de flota**: clusters de UUIDs y patrones por
  fabricante minados de los fingerprints (semilla para heuristicas ML)
- **Soak testing**: `tools/soak_test.py` headless para validacion
  empirica (matrices adaptador x TWS, overnight) con resumen final
- **Documentacion tecnica**: `docs/` con arquitectura, motor BLE,
  pipeline DSP, scoring, analitica, protocolos USB y notas Windows
- CI con `--durations=10` (tests mas lentos visibles)

---

### Historial V1.3 (0.4.0)

### Novedades V1.3
- **Continuidad de audio (packet loss)**: tono continuo + analisis de
  envolvente -> dropouts, hueco total y % de continuidad
- **Session recorder**: cada diagnostico genera `sessions/<ts>_<mac>/`
  con session.json, ble_log.json, waveform.wav y diagnostics.pdf
- **Device Quality Score**: estabilidad/bateria/latencia/audio 0-100 +
  puntaje global ponderado
- **Comparacion automatica**: dos TWS lado a lado o el mismo TWS en el
  tiempo (tendencia de degradacion)
- **Raw BLE logger**: cada advertisement individual con timeline RSSI,
  payloads e intervalos de advertising reales -> tabla `adv_timeline`
- **Analizador de espectro**: FFT + espectrograma (waterfall) de
  capturas de microfono
- **Analitica termica**: temperaturas de carga, sobrecalentamientos y
  tendencia (con medidor USB)
- **Perfilado de firmware**: chipset probable, ecosistema y servicios
  propietarios desde la huella BLE (especulativo)
- **Grabador de curvas de carga**: sondeo continuo del medidor USB con
  alerta de sobrecalentamiento y grafica V/A/W/temp
- **CI/CD**: GitHub Actions (ruff + pytest en Python 3.11/3.12) y
  `pyproject.toml` centralizado
- **Modo Laboratorio** en la UI: dropouts, espectro, raw BLE, score,
  tendencia y comparacion A/B

### Bluetooth BLE
- Escaneo BLE (`bleak`) con auto-refresh cada 5 segundos
- Nombre, MAC, RSSI, fabricante (Company ID) y UUIDs anunciados
- Lectura de bateria via GATT Battery Service (0x180F / 0x2A19)
- Fingerprinting: servicios GATT completos, MTU, manufacturer data,
  RSSI promedio y codecs probables -> tabla `device_capabilities`
- Deteccion de codecs (heuristica): SBC, AAC, aptX, LDAC segun
  fabricante y UUIDs (Windows limita la negociacion real)
- Logging BLE persistente: desconexiones, fallos de conexion, saltos
  de RSSI (>=15 dB) y dispositivos fuera de rango -> tabla `ble_events`

### Diagnostico automatico
- Boton "Diagnosticar auriculares": pipeline bateria GATT -> RMS L/R ->
  latencia -> jitter -> analitica historica -> reporte tecnico
- Reporte PDF multipagina (resumen + graficas de bateria/RSSI) con
  diagnostico final; fallback HTML sin matplotlib

### Analitica
- Salud de bateria: pico reciente vs pico historico (degradacion %)
- Estabilidad de enlace 0-100: varianza RSSI + eventos BLE + jitter
- Estadisticas RSSI historicas por dispositivo

### Tiempo real
- Graficas live en el dashboard (QPainter, sin dependencias):
  RSSI live y bateria live del dispositivo seleccionado
- Estado central `AppState` + bus de eventos pub/sub (`EventBus`)

### Medidores USB (experimental)
- Lector RDTech UM24C/UM25C por puerto serie (voltaje, corriente,
  potencia, mAh, temperatura) -> tabla `charge_history`
- FNB58 / TC66C planificados para V2 (requieren hardware)

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
├── core/                # app_manager, config_manager, app_state,
│                        # event_bus, analytics, diagnostics,
│                        # report_generator, logger
├── ble/                 # motor BLE, lecturas GATT, parser/codecs
├── audio/               # tonos, ruido, sweep, RMS, latencia DSP
├── usb/                 # monitoreo energetico + medidores USB
├── database/            # persistencia SQLite (9 tablas)
├── ui/                  # dashboard, temas, widgets, graficas live
├── exports/             # reportes PDF y exportaciones JSON/CSV
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
