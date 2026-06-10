# Analitica

`core/analytics.py` — version: ANALYTICS_VERSION en `core/versioning.py`.

Todos los indicadores son **probabilisticos**: mejoran con historico
acumulado y se reportan con sus tamanos de muestra.

## battery_health_score

`health = pico_reciente / pico_historico` sobre `battery_history`.
El "pico reciente" usa el ultimo 25 % de lecturas (min 2). Requiere >= 4
lecturas; idealmente varias sesiones de carga completas. Un TWS sano
vuelve a ~100 % tras cargar; si sus maximos caen, la celda degrada.

## stability_score (0-100)

Componentes normalizados a [0, 1] y ponderados:

| Componente | Peso | Normalizador ("totalmente malo")          |
|------------|------|--------------------------------------------|
| rssi       | 0.40 | varianza 100 dB² (sigma 10 dB)             |
| events     | 0.35 | 0.5 eventos adversos por escaneo           |
| jitter     | 0.25 | sigma 50 ms                                 |

Eventos adversos: `disconnected`, `connect_failed`, `out_of_range`.
Pesos se redistribuyen si falta un componente.

## temperature_analytics

Sobre `charge_history.temp_c`: maximo, media, eventos >= 45 C y
tendencia (media de la mitad reciente - mitad antigua). Temperatura de
carga creciente = sintoma clasico de bateria danada.

## comparison

* `compare_devices(a, b)`: scores lado a lado + deltas + ganador.
* `compare_over_time(mac)`: pico de bateria mitad antigua vs reciente;
  `degrading = tendencia < -2 %`.

## profiling_db (mineria de flota)

Agregados sobre todos los fingerprints: clusters de UUIDs (SIG vs
propietarios), patrones por fabricante (codecs tipicos, MTU tipico).
Materia prima para heuristicas ML futuras.

## Acceso a datos

La analitica consume EXCLUSIVAMENTE la API de `DatabaseManager`
(`rssi_values`, `scan_count`, `latest_latency`, `temperatures`, ...).
Nunca SQL directo: eso permite migrar el motor (DuckDB) sin tocarla.
