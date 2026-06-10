# Device Scoring Engine

`core/scoring.py` — version: ver `core/versioning.py` (SCORING_VERSION).

## Metricas (0-100)

| Metrica     | Peso | Fuente                                        |
|-------------|------|-----------------------------------------------|
| estabilidad | 0.30 | `analytics.stability_score`                   |
| bateria     | 0.25 | `analytics.battery_health_score` * 100        |
| latencia    | 0.25 | ultima medicion de `latency_history`          |
| audio       | 0.20 | jitter persistido + extras frescos (RMS, continuidad) |

Si una metrica no tiene datos, su peso se **redistribuye** entre las
presentes (no se castiga a dispositivos con poco historico) y la
metrica aparece en `missing`.

## Formulas

```
latency_score(ms)      = clamp(100 - max(0, ms - 60) * 0.3)
jitter_score(std)      = clamp(100 - std * 2)
rms_balance_score(dB)  = clamp(100 - |dB| * 12)
continuity_score(pct)  = (pct/100)^2 * 100   # cuadratica: castiga huecos
```

La curva cuadratica de continuidad es deliberada: 98 % de continuidad
significa dropouts audibles; no debe puntuar como un 98.

## Extras frescos

El pipeline de diagnostico pasa `extras={"rms_diff_db", "continuity_pct"}`
con los resultados recien medidos (no persistidos por dispositivo), que
se promedian dentro de la metrica de audio.

## Evolucion

Cualquier cambio de formula o peso => incrementar `SCORING_VERSION` en
`core/versioning.py`. Los reportes guardan la version usada; dos scores
solo son comparables con la misma version.
