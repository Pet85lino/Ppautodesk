# Arquitectura

## Vision general

LINO Audio Diagnostic es una suite de diagnostico TWS/Bluetooth con
separacion estricta UI / backend, orientada a eventos.

```
┌─────────────────────────── UI (PySide6) ───────────────────────────┐
│  dashboard.py   widgets.py (LiveChart, BatteryIndicator)  themes   │
└──────────────▲──────────────────────────────▲─────────────────────┘
               │ senales Qt                   │ lectura directa
┌──────────────┴──────────────┐   ┌───────────┴───────────┐
│        AppManager           │   │       AppState        │
│  (orquestador, cableado)    │   │ (estado observable)   │
└───┬──────────┬──────────┬───┘   └───────────────────────┘
    │          │          │
┌───▼───┐ ┌────▼────┐ ┌───▼────────┐      ┌──────────────┐
│ BLE   │ │Database │ │  EventBus  │      │ Diagnostic   │
│Engine │ │Manager  │ │ (pub/sub)  │      │ Runner       │
│QThread│ │ SQLite  │ └────────────┘      │ (pipeline)   │
└───────┘ └─────────┘                     └──────────────┘
```

## Principios

1. **La UI nunca toca asyncio ni SQLite directamente.** Todo llega por
   senales Qt o por la API de `DatabaseManager`.
2. **Toda escritura SQLite ocurre en el hilo principal** (las senales
   del motor BLE se entregan ahi). Un lock + WAL cubren los workers de
   scripts headless.
3. **Los modulos de analisis son funciones puras** sobre datos: se
   testean sin hardware (DSP con senales sinteticas, analytics con DB
   en memoria, charge recorder con lector inyectado).
4. **Degradacion elegante**: sin PortAudio, sin Bluetooth, sin
   matplotlib o sin pyserial la app sigue arrancando; cada subsistema
   reporta su indisponibilidad en logs.

## Hilos

| Hilo                  | Responsabilidad                              |
|-----------------------|----------------------------------------------|
| Principal (Qt)        | UI, persistencia SQLite, orquestacion        |
| BLEEngine (QThread)   | event loop asyncio propio para bleak         |
| Audio workers         | threading.Thread por test (sd.wait bloquea)  |
| ChargeRecorder        | QThread de sondeo del medidor USB            |

Comunicacion entre hilos: SOLO senales Qt (entrega queued automatica).

## Carpetas

Ver README. Regla: `core/` no importa de `ui/`; `ble/`, `audio/` y
`usb/` no se importan entre si (la composicion ocurre en `core/`).
