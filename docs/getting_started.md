# Tutorial: como ejecutar LINO Audio Diagnostic

## Lo primero: NO es un servidor, NO usa puerto

LINO Audio Diagnostic es una **aplicacion de escritorio** (PySide6/Qt).
Cuando la ejecutas se abre una **ventana** directamente, igual que
cualquier programa de Windows. No hay servidor web, no hay puerto, no
hay direccion `localhost:XXXX` que abrir en el navegador.

| Pregunta tipica            | Respuesta                                   |
|----------------------------|---------------------------------------------|
| En que puerto se aloja?    | En ninguno: no es una app web               |
| Donde abro la interfaz?    | Se abre sola como ventana al ejecutarla     |
| Necesito navegador?        | No                                          |
| Corre en segundo plano?    | Solo si usas las herramientas headless (ver seccion 7) |

> Si en el futuro quisieras acceder desde el navegador o desde el
> telefono, eso seria una evolucion del proyecto (un backend FastAPI
> sirviendo el core en un puerto, p.ej. 8000). Hoy no existe.

---

## 1. Requisitos

- **Windows 11** (recomendado; Linux y macOS tambien funcionan)
- **Python 3.11 o superior** — verifica con:
  ```
  python --version
  ```
- **Adaptador Bluetooth** con soporte BLE (el integrado del laptop sirve)
- Auriculares TWS para diagnosticar (Maxell, Xiaomi, JBL, etc.)

## 2. Descargar el proyecto

```bash
git clone https://github.com/Pet85lino/Ppautodesk.git
cd Ppautodesk
git checkout claude/adoring-ride-gtuarm
```

(O descarga el ZIP desde GitHub y descomprimelo.)

## 3. Crear el entorno virtual (recomendado)

Aisla las dependencias del resto de tu sistema:

```bash
python -m venv .venv

# Windows (PowerShell o CMD):
.venv\Scripts\activate

# Linux/macOS:
source .venv/bin/activate
```

Sabras que esta activo porque el prompt muestra `(.venv)`.

## 4. Instalar dependencias

```bash
pip install -r requirements.txt
```

Esto instala bleak (Bluetooth), PySide6 (interfaz), numpy/scipy (DSP),
sounddevice (audio), matplotlib (graficas/PDF), pyserial (medidores
USB), psutil y pandas. Tarda unos minutos la primera vez.

## 5. Ejecutar la aplicacion

```bash
python main.py
```

Se abre la ventana del dashboard (tema oscuro). En la consola veras
los logs en vivo:

```
13:41:24 | INFO | lino.core.app    | LINO Audio Diagnostic v0.6.0 inicializando...
13:41:24 | INFO | lino.database    | Base de datos lista en .../lino_diagnostic.db (WAL)
13:41:24 | INFO | lino.ble.scanner | Motor BLE iniciado (loop asyncio en hilo dedicado)
13:41:25 | INFO | lino.ble.scanner | Escaneando BLE durante 4.0 s...
```

La app escanea Bluetooth automaticamente **cada 5 segundos**.

## 6. Tour de la interfaz (panel lateral)

### Dashboard
- Tabla de dispositivos BLE cercanos: nombre, MAC, RSSI, fabricante,
  bateria. Se refresca sola cada 5 s.
- Selecciona un dispositivo y usa:
  - **Leer bateria** — conecta por GATT y lee el nivel (si el TWS lo
    expone; muchos usan protocolos propietarios y mostraran "N/D").
  - **Fingerprint** — captura servicios GATT, MTU y codecs probables.
  - **Diagnosticar auriculares** — pipeline completo: bateria + RMS +
    latencia + jitter + continuidad + reporte PDF + sesion.
- Graficas live de RSSI y bateria del dispositivo seleccionado.
- **Exportar JSON / CSV** — vuelca todo el historial a `exports/`.

### Audio
- Selecciona la **salida** (tus auriculares) y el **microfono**.
  IMPORTANTE: conecta los TWS al sistema como dispositivo de audio
  antes de los tests.
- Tests: tono L/R, balance, ruido blanco/rosa, sweep 20 Hz-20 kHz,
  RMS L/R, latencia, jitter, sync L/R y perfil de microfono.

### Energia
- Estado de carga del equipo y medidores USB detectados (UM25C, etc.).

### Laboratorio
- Dropouts (packet loss), espectro, raw BLE log, Device Quality Score,
  tendencia temporal y comparacion A/B entre dos dispositivos.

### Logs
- Consola tecnica en vivo (lo mismo que ves en la terminal).

## 7. Herramientas de linea de comandos (sin ventana)

Para sesiones largas de validacion (overnight) existe el soak test
headless — este SI corre "como servicio" en la terminal, pero tampoco
usa ningun puerto:

```bash
# 8 horas de escaneos continuos cada 5 s, con resumen final:
python tools/soak_test.py --hours 8 --interval 5

# 10 minutos leyendo ademas la bateria de una MAC concreta:
python tools/soak_test.py --minutes 10 --battery AA:BB:CC:DD:EE:FF
```

Se detiene con `Ctrl+C` mostrando el resumen (escaneos, errores,
eventos BLE, memoria).

## 8. Verificar la instalacion (tests)

```bash
python -m unittest discover tests
```

Deberias ver `OK` con 86 tests. No requieren Bluetooth ni audio.

## 9. Donde quedan tus datos

| Carpeta / archivo        | Contenido                                  |
|--------------------------|---------------------------------------------|
| `lino_diagnostic.db`     | Base SQLite con TODO el historial          |
| `logs/lino.log`          | Log rotativo de la aplicacion              |
| `exports/`               | Exportaciones JSON/CSV y reportes PDF      |
| `sessions/<fecha>_<mac>/`| Sesiones de diagnostico completas          |
| `config.json`            | Configuracion (intervalos, reintentos BLE) |

## 10. Problemas comunes

| Sintoma | Solucion |
|---------|----------|
| `Adaptador Bluetooth no disponible` | Enciende el Bluetooth en Windows; verifica que el adaptador aparezca en el Administrador de dispositivos |
| La tabla queda vacia | Los TWS deben estar FUERA del case y en modo visible/anunciando |
| `sounddevice no disponible` | `pip install sounddevice`; en Linux instala ademas PortAudio (`sudo apt install libportaudio2`) |
| Bateria siempre "N/D" | Ese TWS no expone el Battery Service estandar (limitacion del fabricante, no de la app) |
| La app no abre en Linux sin escritorio | Qt necesita pantalla; para probar sin GUI: `QT_QPA_PLATFORM=offscreen python main.py` |
| Conecto el case por USB-C al PC y "no conecta" | Es normal: el USB-C del case solo tiene pines de carga (sin lineas de datos). Windows no enumera nada y NINGUN software puede leer bateria por ese cable. Auriculares -> bateria por Bluetooth; carga del case -> medidor USB en linea (UM25C) entre cargador y case |
| Auto-refresh se vuelve lento | El watchdog detecto fallos del adaptador y escalo el intervalo; se restaura solo al recuperarse |

## 11. Configuracion rapida (config.json)

```json
"scan":    { "auto_refresh_ms": 5000 },   // frecuencia de escaneo
"ble":     { "retry_attempts": 3 },        // reintentos de conexion
"logging": { "level": "INFO" },            // "DEBUG" para investigar fallos
"perf":    { "monitor_enabled": false }    // true en sesiones overnight
```
