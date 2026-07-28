# TelegramRegexSearch

Busca patrones de expresiones regulares dentro de **tus propios** historiales
de Telegram exportados, en formato JSON y HTML.

- Python 3.10 o superior.
- **El núcleo usa solo la librería estándar**: nada que instalar.
- Compatible con Windows, Linux y Pydroid 3 (Android).
- Procesamiento en streaming: un export de 48 MB se analiza con un pico de
  **1,3 MB** de memoria.
- 145 pruebas automatizadas incluidas.

---

## Índice

1. [Instalación](#instalación)
2. [Cómo exportar tu historial](#cómo-exportar-tu-historial-de-telegram)
3. [Uso rápido](#uso-rápido)
4. [Patrones](#definir-tus-patrones)
5. [Configuración](#configuración)
6. [Resultados](#resultados)
7. [Logs](#logs)
8. [Opciones de consola](#opciones-de-consola)
9. [Descarga en vivo (opcional)](#descarga-en-vivo-opcional)
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

**Pydroid 3:** copia la carpeta al almacenamiento del dispositivo, abre
`main.py` desde la app y pulsa el botón de ejecutar. No hace falta instalar
ningún paquete desde el gestor de pip.

---

## Cómo exportar tu historial de Telegram

1. Abre **Telegram Desktop** (la exportación no está disponible en móvil).
2. Menú del chat (⋮) → **Exportar historial de chat**.
   - Para todo: **Ajustes → Avanzado → Exportar datos de Telegram**.
3. Elige el formato **JSON** (o HTML, ambos funcionan).
4. Copia el `result.json` (o los `messages*.html`) dentro de `datos/`.

Puedes poner varios exports a la vez, incluso en subcarpetas: se recorren
todos. Funcionan tanto el export de un chat suelto como el export completo
de la cuenta con todos los chats en un solo archivo.

---

## Uso rápido

```bash
# 1. Pon tus exports en datos/
# 2. Edita patrones.txt con lo que quieras buscar
# 3. Ejecuta
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
| `--sin-progreso` | Desactiva la barra de progreso. |
| `--verbose` | Muestra también los mensajes de depuración. |

Ejemplos:

```bash
python main.py --modo finditer
python main.py --datos "D:/exports_telegram" --patrones "mis_patrones.txt"
python main.py --verbose --sin-progreso
```

**Códigos de salida:** `0` correcto · `1` error · `2` nada que hacer (sin
patrones o sin datos) · `130` interrumpido con Ctrl+C.

---

## Descarga en vivo (opcional)

> Esta parte es **opcional** y es lo único que necesita un paquete externo.
> Si no la usas, ignórala: el proyecto funciona entero sin ella.

En lugar de exportar a mano desde Telegram Desktop, se pueden descargar los
historiales directamente.

### Un aviso importante sobre la API

**La API de Telegram no admite expresiones regulares.** `messages.search`
solo busca por subcadena o palabra clave, opcionalmente acotada por tipo de
contenido. Por eso la descarga **no sustituye** al motor de búsqueda: trae
los mensajes, los guarda en `datos/` con el formato de export, y el regex se
aplica en local exactamente igual que siempre.

Sí conviene usar `--filtro` y `--consulta` para que el servidor acote lo que
envía: descargar solo los mensajes con enlaces es mucho más rápido que
bajarlo todo para luego aplicar un regex de URLs.

### Configuración

```bash
pip install telethon
```

Luego, las credenciales de https://my.telegram.org, **por variables de
entorno** (recomendado):

```bash
# Windows (PowerShell)
$env:TELEGRAM_API_ID="TU_API_ID"
$env:TELEGRAM_API_HASH="TU_API_HASH"

# Linux / macOS / Termux
export TELEGRAM_API_ID="TU_API_ID"
export TELEGRAM_API_HASH="TU_API_HASH"
```

O copiando `credenciales.ejemplo.json` a `credenciales.json` y rellenándolo.
Ese archivo está en `.gitignore` para que no acabe en un repositorio.

### Uso

```bash
# Descargar todo y analizarlo
python main.py --descargar

# Solo un chat, con límite
python main.py --descargar --chat @usuario --limite 5000

# Solo mensajes con enlaces (filtrado en el servidor)
python main.py --descargar --filtro enlaces --modo finditer

# Instalar Telethon automáticamente si falta
python main.py --descargar --instalar-dependencias
```

| Opción | Qué hace |
|---|---|
| `--descargar` | Activa la descarga previa al análisis. |
| `--chat CHAT` | Chat concreto (repetible). Por defecto, todos. |
| `--limite N` | Máximo de mensajes por chat. |
| `--consulta TEXTO` | Búsqueda por texto en el servidor (no regex). |
| `--filtro TIPO` | `todos`, `enlaces`, `fotos`, `videos`, `documentos`, `musica`, `voz`, `gifs`, `menciones`, `fijados`, `contactos`, `ubicaciones`. |
| `--instalar-dependencias` | Instala con pip lo que falte. |

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
│   └── descargador.py            # Descarga MTProto (opcional)
├── io_utils/
│   ├── exportador.py             # Escribe los .txt de resultados
│   └── progress.py               # Barra de progreso
├── utils/
│   ├── logger_setup.py           # Logging a proceso.log y error.log
│   ├── filesystem.py             # Carpetas y saneado de nombres
│   ├── dependencias.py           # Verifica/instala requisitos
│   └── credenciales.py           # Carga segura de credenciales
├── tests/                        # 145 pruebas
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
`config.json`.

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
