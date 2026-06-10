# Pipeline DSP de audio

Todos los analisis son funciones puras (numpy/scipy) testeables con
senales sinteticas; `sounddevice` solo aporta la captura/reproduccion.
Sample rate global: 48 kHz (estandar A2DP).

## Latencia (`audio/latency_test.py`)

1. Chirp logaritmico 1-4 kHz (`scipy.signal.chirp`) con fundido Hann al
   10 % en cada extremo (sin clics -> correlacion limpia).
2. `sd.playrec`: reproduccion y grabacion con el mismo reloj de audio.
3. Normalizacion (media 0, energia 1) de ambas senales.
4. Correlacion cruzada `method='fft'` (O(n log n)).
5. **Confidence score**: peak-to-sidelobe ratio del pico, excluyendo
   ±5 ms alrededor; PSR de 20 sigmas -> confianza 1.0. Mediciones con
   confianza < 0.25 se descartan.

Limitacion: incluye la latencia del stack de audio del SO; el valor es
comparativo entre auriculares/codecs, no absoluto.

## Jitter y sincronizacion

* `jitter_test(runs=N)`: media + desviacion estandar de N mediciones
  validas. La sigma es el indicador critico de estabilidad Bluetooth.
* `stereo_sync_test()`: latencia por canal (L solo / R solo) -> drift
  entre auriculares TWS.

## Continuidad / packet loss (`audio/dropout_test.py`)

1. Tono continuo de 1 kHz reproducido y grabado en loopback.
2. Envolvente RMS por ventanas de 10 ms.
3. Region activa = primera a ultima ventana sobre el piso de ruido.
4. Dropout = ventana activa < 15 % de la mediana de la region.
5. Salida: numero de dropouts, hueco total en ms, % de continuidad.

## Espectro (`audio/spectrum.py`)

* FFT con ventana Hann -> magnitud dB (20 Hz - 20 kHz, eje log).
* Espectrograma `scipy.signal.spectrogram` (nperseg 1024, overlap 512).
* Uso tipico: ruido rosa o sweep por los auriculares + captura de mic
  -> respuesta en frecuencia real aproximada.

## Proteccion auditiva (`audio/audio_test.py`)

Limites DUROS aplicados a toda reproduccion: amplitud max 0.6,
duracion max 10 s. Cubiertos por tests.
