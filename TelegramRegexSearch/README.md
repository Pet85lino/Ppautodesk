# TelegramRegexSearch

Busca patrones de expresiones regulares dentro de **tus propios** historiales
de Telegram exportados, en formato JSON y HTML.

- Python 3.10 o superior.
- **El núcleo usa solo la librería estándar**: nada que instalar.
- Compatible con Windows, Linux y Pydroid 3 (Android).
- Procesamiento en streaming: un export de 48 MB se analiza con un pico de
  **1,3 MB** de memoria.
- 214 pruebas automatizadas incluidas.

---

## Índice

1. [Instalación](#instalación)
2. [Cómo conseguir los mensajes](#cómo-conseguir-los-mensajes)
3. [Uso rápido](#uso-rápido)
4. [Patrones](#definir-tus-patrones)
5. [Configuración](#configuración)
6. [Resultados](#resultados)
7. [Logs](#logs)
8. [Opciones de consola](#opciones-de-consola)
9. [Descarga en vivo (recomendado)](#descarga-en-vivo-recomendado)
10. [Pruebas](#ejecutar-las-pruebas)
11. [Estructura](#estructura-del-proyecto)
12. [Notas de diseño](#notas-de-diseño)
13. [Solución de problemas](#solución-de-problemas)

---

## Instalación

No hay instalación. Descomprime la carpeta y ejecuta:

```bash
python main.py
```

La primera ejecución crea `config.json` y las carpetas `datos/`,
`resultados/`, `logs/` y `cache/` si no existen.

### Pydroid 3 (Android)

Copia la carpeta al almacenamiento del dispositivo, abre `main.py` desde la
app y pulsa el botón de ejecutar. Para analizar exports **no hace falta
instalar ningún paquete**; solo la descarga en vivo necesita Telethon, y el
asistente se encarga de instalarlo.

Si la dejas en la ubicación habitual de Pydroid, las rutas quedan así:

```
/storage/emulated/0/QPYTHON/TelegramRegexSearch/
```

que en cualquier explorador de archivos verás como:

```
Almacenamiento interno/QPYTHON/TelegramRegexSearch/
```

Los exports van en la subcarpeta `datos/` de esa ruta.

> **Ojo:** Telegram para Android **no puede exportar historiales** — esa
> opción solo existe en Telegram Desktop. Desde el móvil, usa
> `python main.py --configurar` y después
> [`--descargar`](#descarga-en-vivo-recomendado): la aplicación se conecta a
> Telegram por su cuenta y no necesita ningún PC.

---

## Cómo conseguir los mensajes

Hay dos caminos. **El primero funciona en cualquier parte y es el
recomendado.**

### Camino A: conectar con Telegram (recomendado)

La aplicación se conecta a Telegram y trae los mensajes ella misma. Funciona
en **Windows, Linux y Android (Pydroid 3)**, y no necesita ningún cliente de
escritorio.

```bash
python main.py --configurar
```

El asistente instala lo que falte, te guía para obtener tus credenciales, te
pide el teléfono y el código de verificación, y comprueba que todo funciona.
Solo hay que hacerlo una vez: la sesión queda guardada.

Después:

```bash
python main.py --descargar --tipo grupos --dias 30
```

Detalles completos en [Descarga en vivo](#descarga-en-vivo-recomendado).

### Camino B: exportar a mano desde Telegram Desktop

1. Abre **Telegram Desktop**.
2. Menú del chat (⋮) → **Exportar historial de chat**.
   - Para todo: **Ajustes → Avanzado → Exportar datos de Telegram**.
3. Elige el formato **JSON** (o HTML, ambos funcionan).
4. Copia el `result.json` (o los `messages*.html`) dentro de `datos/`.

> **Esta opción solo existe en Telegram Desktop.** Ni la aplicación de
> Android ni Telegram Web tienen exportación de historiales: si la estás
> buscando ahí, no es que no la encuentres, es que no está. Usa el camino A.

Puedes poner varios exports a la vez, incluso en subcarpetas: se recorren
todos. Funcionan tanto el export de un chat suelto como el export completo
de la cuenta con todos los chats en un solo archivo.

---

## Uso rápido

```bash
# 1. Consigue los mensajes (una de las dos vías de arriba)
python main.py --configurar          # conectar con Telegram, la primera vez
python main.py --descargar --dias 30 # traer los mensajes

# 2. Edita patrones.txt con lo que quieras buscar

# 3. Analiza
python main.py
```

Aparecerá una línea de progreso que se va actualizando:

```
Archivos: 3 | Mensajes: 152340 | Regex: 913404 | Coincidencias: 87 | Tiempo: 01:12 | Msj/s: 2116
```

Al terminar tendrás un `.txt` por cada patrón con coincidencias dentro de
`resultados/`.

---

## Definir tus patrones

Edita `patrones.txt`. Un patrón por línea:

```
# Las líneas que empiezan con # son comentarios.
# Las líneas vacías se ignoran.

correos :: [a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}
urls :: https?://[^\s]+
\bfactura\b
```

- La sintaxis `nombre :: regex` da nombre al patrón, y ese nombre será el
  del archivo de resultados (`resultados/correos.txt`).
- Sin nombre se usa `patron_<numero_de_linea>`.
- Para que **un** patrón ignore mayúsculas, ponle el prefijo `(?i)`. Para
  que lo hagan **todos**, pon `"ignorar_mayusculas": true` en `config.json`.
- Si un patrón no compila, se anota en `logs/error.log` y **el resto sigue
  funcionando**: no se aborta la ejecución.

---

## Configuración

`config.json` se crea solo la primera vez. Todos los valores se validan al
cargarlos: si alguno es inválido, se sustituye por el valor por defecto y se
avisa en el log (nunca falla en silencio).

| Clave | Por defecto | Qué hace |
|---|---|---|
| `carpeta_datos` | `"datos"` | Dónde buscar los exports. |
| `carpeta_resultados` | `"resultados"` | Dónde escribir los `.txt`. |
| `carpeta_logs` | `"logs"` | Dónde escribir los logs. |
| `carpeta_cache` | `"cache"` | Reservada (sesión MTProto). |
| `archivo_patrones` | `"patrones.txt"` | Archivo de patrones. |
| `extensiones_soportadas` | `[".json"]` | Añade `".html"` para analizar también HTML. |
| `modo_busqueda` | `"search"` | `search`, `findall` o `finditer`. |
| `ignorar_mayusculas` | `false` | Aplica `re.IGNORECASE` a todos los patrones. |
| `evitar_duplicados` | `true` | Descarta coincidencias repetidas. |
| `dias_recientes` | `[]` | Ventanas temporales en días, p. ej. `[30, 60, 90, 160, 180]`. |
| `fecha_desde` | `""` | Fecha mínima `AAAA-MM-DD`. |
| `fecha_hasta` | `""` | Fecha máxima `AAAA-MM-DD`. |
| `actualizar_progreso_cada_n_mensajes` | `200` | Frecuencia de refresco de la barra. |
| `codificacion_salida` | `"utf-8"` | Codificación de los `.txt`. |
| `max_archivos_resultado_abiertos` | `32` | Descriptores abiertos a la vez. |
| `tam_buffer_lectura_kb` | `256` | Tamaño del bloque de lectura. |

### Los tres modos de búsqueda

| Modo | Qué devuelve | Cuándo usarlo |
|---|---|---|
| `search` | La **primera** coincidencia de cada patrón en cada mensaje. | El más rápido. Para saber *qué mensajes* contienen algo. |
| `finditer` | **Todas** las coincidencias, con su posición. | El más completo. Para extraer *todos* los datos. |
| `findall` | Todas las coincidencias, sin posición. | Cuando la posición no importa. |

---

## Rangos de fechas

Se puede acotar el análisis a un periodo, por consola o desde `config.json`.

### Una sola ventana

```bash
python main.py --dias 30                      # últimos 30 días
python main.py --desde 2024-01-01             # desde una fecha
python main.py --desde 2024-01-01 --hasta 2024-06-30   # entre dos fechas
```

Los resultados van a `resultados/` como siempre.

### Varias ventanas a la vez

```bash
python main.py --dias 30,60,90,160,180
```

Genera una subcarpeta por ventana:

```
resultados/
├── ultimos_30_dias/
├── ultimos_60_dias/
├── ultimos_90_dias/
├── ultimos_160_dias/
└── ultimos_180_dias/
```

Las ventanas son **acumulativas**: un mensaje de hace 10 días aparece en las
cinco; uno de hace 100 aparece solo en las de 160 y 180. Todo se resuelve en
**un único recorrido** de los archivos, así que pedir cinco ventanas cuesta
prácticamente lo mismo que pedir una.

### Desde config.json

```json
{
    "dias_recientes": [30, 60, 90, 160, 180],
    "fecha_desde": "",
    "fecha_hasta": ""
}
```

Las opciones de consola tienen prioridad sobre `config.json`, de modo que la
configuración fija tu comportamiento habitual y la consola te permite
desviarte puntualmente sin editar nada.

> **Sobre las fechas:** se compara la hora local que muestra Telegram, la
> misma que ves en la aplicación. Si la fecha de un mensaje no se puede
> interpretar, ese mensaje **se incluye** en lugar de descartarse: perder una
> coincidencia real por un problema de formato sería peor que mostrarla de
> más. El log avisa de cuántos hubo.

---

## ¿De quién son las coincidencias?

De **todos los participantes**, no solo tuyas. El buscador recorre cada
mensaje de cada chat sin mirar quién lo escribió, y cada resultado indica su
autor en el campo `Usuario:`. En un grupo con cien personas verás las
coincidencias de las cien.

---

## Resultados

Un archivo por patrón, en UTF-8, con este formato por coincidencia:

```
==================================================
Archivo: result.json
Chat: Chat con Ana
Usuario: Ana
Fecha: 2024-01-01T10:00:00
ID mensaje: 1

Regex: [a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}

Coincidencia: ana@ejemplo.com
Posicion: 13
Coincidencias en el mensaje: 1

Mensaje completo:
mi correo es ana@ejemplo.com, escribeme
==================================================
```

Cada ejecución **reemplaza** los resultados anteriores, para que nunca
tengas dudas sobre si lo que ves es de esta ejecución o de una antigua.

---

## Logs

- `logs/proceso.log` — inicio, fin, archivos procesados, tiempo total,
  coincidencias y resumen final.
- `logs/error.log` — solo errores: regex inválidos, archivos corruptos,
  problemas de escritura.

Si algo no sale como esperabas, **`error.log` es el primer sitio donde
mirar**.

---

## Opciones de consola

```bash
python main.py [opciones]
```

| Opción | Qué hace |
|---|---|
| `--base-dir RUTA` | Carpeta base del proyecto. |
| `--modo {search,findall,finditer}` | Modo de búsqueda (manda sobre `config.json`). |
| `--datos RUTA` | Analiza exports de otra carpeta. |
| `--patrones RUTA` | Usa otro archivo de patrones. |
| `--dias N[,N...]` | Solo los últimos N días. Admite varias ventanas. |
| `--desde AAAA-MM-DD` | Fecha mínima. |
| `--hasta AAAA-MM-DD` | Fecha máxima. |
| `--sin-progreso` | Desactiva la barra de progreso. |
| `--verbose` | Muestra también los mensajes de depuración. |

Ejemplos:

```bash
python main.py --modo finditer
python main.py --datos "D:/exports_telegram" --patrones "mis_patrones.txt"
python main.py --dias 30,60,90,160,180 --modo finditer
python main.py --verbose --sin-progreso
```

**Códigos de salida:** `0` correcto · `1` error · `2` nada que hacer (sin
patrones o sin datos) · `130` interrumpido con Ctrl+C.

---

## Descarga en vivo (recomendado)

Trae los mensajes directamente desde Telegram, sin exportar nada a mano y sin
depender de Telegram Desktop. Funciona en Windows, Linux y Android.

Es lo único del proyecto que necesita un paquete externo: **Telethon**. El
asistente lo instala solo.

### Configuración, una sola vez

```bash
python main.py --configurar
```

El asistente hace todo en orden:

1. **Instala Telethon** si no está.
2. **Te guía para obtener tus credenciales.** Necesitas un `api_id` y un
   `api_hash` propios, gratuitos, desde https://my.telegram.org →
   *API development tools*. Se abre desde cualquier navegador, también el del
   móvil.
3. **Te pide el teléfono** con prefijo internacional (`+34600111222`).
4. **Te pide el código de verificación.** Llega a tu **aplicación de
   Telegram**, no por SMS (salvo que no tengas ninguna sesión abierta).
5. **Te pide la contraseña de dos pasos**, si tienes esa protección activada.
6. **Comprueba que funciona** contando tus chats accesibles.

Las credenciales se guardan en `credenciales.json` y la sesión en
`cache/telegram.session`. Ambos están en `.gitignore`. **El archivo de sesión
da acceso a tu cuenta: no lo compartas.**

Si prefieres no guardar las credenciales en un archivo, puedes usar variables
de entorno, que tienen prioridad:

```bash
# Windows (PowerShell)
$env:TELEGRAM_API_ID="TU_API_ID"
$env:TELEGRAM_API_HASH="TU_API_HASH"

# Linux / macOS / Termux
export TELEGRAM_API_ID="TU_API_ID"
export TELEGRAM_API_HASH="TU_API_HASH"
```

### Un aviso importante sobre la API

**La API de Telegram no admite expresiones regulares.** `messages.search`
solo busca por subcadena o palabra clave, opcionalmente acotada por tipo de
contenido. Por eso la descarga **no sustituye** al motor de búsqueda: trae
los mensajes, los guarda en `datos/` con el formato de export, y el regex se
aplica en local exactamente igual que siempre.

Sí conviene usar `--filtro` y `--consulta` para que el servidor acote lo que
envía: descargar solo los mensajes con enlaces es mucho más rápido que
bajarlo todo para luego aplicar un regex de URLs.

### Si la instalación automática falla

Ocurre cuando pip intenta compilar una dependencia y el entorno no puede.

- **Pydroid 3:** menú lateral → *Pip* → *Install* → escribe `telethon` →
  *Install*. Usa paquetes ya compilados, así que funciona aunque pip por
  consola falle.
- **Windows:** `py -m pip install telethon`

Después vuelve a lanzar `python main.py --configurar`.

### Paso 1: ver qué grupos y canales tienes

```bash
python main.py --listar-chats --tipo grupos
```

```
TIPO                  ID  USUARIO              NOMBRE
------------------------------------------------------------------------------
grupo        -1001234567                       Equipo Proyecto
grupo        -1009876543  @grupo_publico       Comunidad Python
canal        -1005555555  @noticias_tech       Noticias Tech

3 chat(s). Usa --chat con el ID, el @usuario o parte del nombre.
```

Este paso importa porque **los grupos privados no tienen `@usuario`**: la
única forma de referirse a ellos es por su ID o por su nombre.

### Paso 2: buscar en los que te interesen

```bash
# Todos los grupos a los que perteneces
python main.py --descargar --tipo grupos --dias 30

# Grupos concretos: por nombre (parcial), por @usuario o por ID
python main.py --descargar --chat "Equipo Proyecto" --chat @noticias_tech

# Solo canales, últimos 90 días, todas las coincidencias
python main.py --descargar --tipo canales --dias 90 --modo finditer

# Acotar en el servidor antes de descargar: mucho más rápido
python main.py --descargar --tipo grupos --filtro enlaces --limite 5000
```

| Opción | Qué hace |
|---|---|
| `--configurar` | Asistente de acceso a Telegram: credenciales y código. |
| `--descargar` | Activa la descarga previa al análisis. |
| `--listar-chats` | Muestra los chats accesibles y termina. |
| `--tipo TIPO` | `todos`, `grupos`, `canales` o `privados`. |
| `--chat CHAT` | Chat concreto por ID, `@usuario` o parte del nombre. Repetible. |
| `--limite N` | Máximo de mensajes por chat. |
| `--consulta TEXTO` | Búsqueda por texto en el servidor (no regex). |
| `--filtro TIPO` | `todos`, `enlaces`, `fotos`, `videos`, `documentos`, `musica`, `voz`, `gifs`, `menciones`, `fijados`, `contactos`, `ubicaciones`. |
| `--espera-maxima N` | Segundos máximos de espera ante un límite de Telegram. |
| `--instalar-dependencias` | Instala con pip lo que falte. |

`--tipo grupos` incluye los supergrupos. `--tipo canales` deja fuera los
supergrupos y se queda solo con los canales de difusión, que es lo que uno
espera aunque Telegram los modele internamente igual.

### Sobre los límites de Telegram

Recorrer todos tus grupos dispara los límites de peticiones del servidor
(`FloodWait`). El descargador lo espera y **reanuda desde el último mensaje
recibido**, sin repetir ni perder ninguno. Si Telegram pide más tiempo del
indicado en `--espera-maxima`, ese chat se deja a medias y se continúa con el
siguiente, dejando constancia en el log.

Cada chat se escribe primero en un archivo `.parcial` y se renombra al
terminar, así que una descarga interrumpida nunca deja un JSON truncado en
`datos/`. Descargar todo un historial puede tardar horas: empieza con
`--limite` y `--dias` para hacerte una idea.

La primera vez se pedirá tu teléfono y el código de confirmación. Se crea un
archivo `.session` en `cache/`: **da acceso a tu cuenta**, no lo compartas
(también está en `.gitignore`).

---

## Ejecutar las pruebas

```bash
python ejecutar_tests.py          # toda la suite
python ejecutar_tests.py -v       # con detalle
python ejecutar_tests.py test_exportador   # un módulo suelto
```

Solo usa `unittest` de la librería estándar, así que funciona igual en
Windows y en Pydroid 3. Consulta [AUDITORIA.md](AUDITORIA.md) para el
informe completo de la revisión.

---

## Estructura del proyecto

```
TelegramRegexSearch/
├── main.py                       # Punto de entrada, orquesta el pipeline
├── ejecutar_tests.py             # Lanzador de la suite de pruebas
├── patrones.txt                  # Tus patrones regex
├── config.json                   # Se crea en la primera ejecución
├── credenciales.ejemplo.json     # Plantilla (solo descarga en vivo)
├── AUDITORIA.md                  # Informe de auditoría
├── config/
│   └── config_manager.py         # Carga y valida config.json
├── core/
│   ├── models.py                 # Mensaje, PatronRegex, Coincidencia
│   ├── json_stream.py            # Lector JSON incremental
│   ├── parser_json.py            # Normaliza mensajes desde JSON
│   ├── parser_html.py            # Normaliza mensajes desde HTML
│   ├── regex_loader.py           # Lee y compila patrones.txt
│   ├── search_engine.py          # Aplica los regex
│   ├── filtro_fechas.py          # Ventanas temporales
│   ├── sesion.py                 # Login en Telegram (opcional)
│   └── descargador.py            # Descarga MTProto (opcional)
├── io_utils/
│   ├── exportador.py             # Escribe los .txt de resultados
│   └── progress.py               # Barra de progreso
├── utils/
│   ├── logger_setup.py           # Logging a proceso.log y error.log
│   ├── filesystem.py             # Carpetas y saneado de nombres
│   ├── dependencias.py           # Verifica/instala requisitos
│   └── credenciales.py           # Carga segura de credenciales
├── tests/                        # 214 pruebas
├── datos/                        # Tus exports (vacía al empezar)
├── resultados/                   # Salida
├── logs/                         # proceso.log y error.log
└── cache/                        # Sesión MTProto
```

---

## Notas de diseño

**Streaming de principio a fin.** El lector JSON recorre el archivo por
bloques y decodifica un mensaje cada vez, en lugar de usar `json.load()`.
Medido sobre un export sintético de 48,6 MB con 150.000 mensajes:

| Método | Pico de memoria |
|---|---|
| `json.load()` | 147,97 MB |
| Lector incremental | **1,26 MB** |

Es la diferencia entre funcionar y no funcionar en un móvil.

**Un solo contrato de mensaje.** JSON, HTML y la descarga en vivo producen
todos objetos `Mensaje` idénticos, así que el motor de búsqueda no sabe —ni
necesita saber— de dónde vienen los datos.

**Los errores aíslan, no propagan.** Un regex inválido, un JSON corrupto o
un chat inaccesible se registran y se saltan; el resto del trabajo continúa.
Analizar 40 exports no debería fracasar porque uno esté dañado.

**Recursos acotados.** Los descriptores de archivo abiertos tienen un tope
(caché LRU) y el filtro de duplicados también, desactivándose con un aviso
antes que agotar la memoria.

**La consola es un entorno hostil.** La barra de progreso se adapta al ancho
de la terminal, se desactiva si la salida no es interactiva, y se coordina
con el logging para que los mensajes no la pisen. El handler de consola
sustituye los caracteres que la página de códigos de Windows no admita en
lugar de reventar con `UnicodeEncodeError`.

---

## Solución de problemas

**"No hay archivos que analizar"** — Los exports deben estar dentro de
`datos/` (o de la carpeta que indiques con `--datos`) y tener extensión
`.json`. Para HTML, añade `".html"` a `extensiones_soportadas` en
`config.json`. El mensaje incluye la ruta exacta donde está buscando: cópiala
tal cual en tu explorador de archivos para comprobar dónde debe ir el export.

**Cargó menos patrones de los que escribí** — Si `patrones.txt` viaja entre
Windows y Android por ciertos medios puede perder los saltos de línea y
quedar todo en una sola línea, que se interpretaría como un único patrón.
El log dice cuántos cargó (`Patrones cargados: N válidos`): si el número no
cuadra con tus líneas, ábrelo en un editor y comprueba que cada patrón está
en su propia línea.

**"No hay ningún patrón válido"** — `patrones.txt` está vacío o todas sus
líneas son comentarios. Mira `logs/error.log` para ver si algún patrón no
compiló.

**No encuentra coincidencias que sé que existen** — Prueba
`"ignorar_mayusculas": true`, o el prefijo `(?i)` en el patrón. Recuerda que
`search` solo devuelve la primera coincidencia de cada mensaje: usa
`--modo finditer` para verlas todas.

**Va muy lento o parece colgado** — Revisa `logs/proceso.log`: si aparece un
aviso de "retroceso catastrófico", hay un patrón con anidamientos del tipo
`(a+)+` que puede tardar un tiempo desproporcionado. Acota los
cuantificadores.

**La barra de progreso deja basura en pantalla** — Ejecuta con
`--sin-progreso`. Ocurre en terminales que no informan bien de su ancho.

**Los acentos salen mal en los resultados** — Ábrelos con un editor que
respete UTF-8 (el Bloc de notas moderno lo hace; algunas versiones antiguas
de Excel no).
