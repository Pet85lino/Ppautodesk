# Informe de auditoría — TelegramRegexSearch

Revisión completa del código previo, corrección de los defectos encontrados y
verificación de que las correcciones funcionan.

- **Defectos corregidos:** 20
- **Pruebas automatizadas:** 197 (196 ejecutadas, 1 omitida por requerir Telethon)
- **Resultado:** todas en verde

```
$ python ejecutar_tests.py
Ran 197 tests in 0.13s
OK (skipped=1)
Resumen: 197 pruebas | 0 fallos | 0 errores | 1 omitidas
```

---

## 1. Defectos críticos

### 1.1 El archivo entero se cargaba en memoria

**Dónde:** `parser_json.py`
**Severidad:** crítica

`json.load()` construye en memoria el árbol completo del JSON. Con un export
real de cientos de megabytes, el proceso muere por falta de memoria en
Pydroid 3 y consume varios gigabytes en Windows. Contradecía de raíz el
requisito de "poca memoria / usar iteradores": los generadores del código
anterior iteraban sobre una estructura **ya cargada del todo**.

**Corrección:** nuevo módulo `core/json_stream.py`, un lector JSON
incremental hecho solo con la librería estándar. Recorre el archivo por
bloques y decodifica un mensaje cada vez con `JSONDecoder.raw_decode()`,
descartando la parte ya consumida.

**Verificación** (export sintético de 48,6 MB, 150.000 mensajes):

| Método | Pico de memoria | Mensajes leídos |
|---|---|---|
| `json.load()` (código anterior) | 147,97 MB | 150.000 |
| Lector incremental (actual) | **1,26 MB** | 150.000 |

**118 veces menos memoria**, con resultados idénticos. El pico ya no depende
del tamaño del archivo, sino del bloque de lectura.

Las pruebas de `tests/test_json_stream.py` se ejecutan con bloques de 8 a 64
bytes para que cualquier dependencia oculta de "tener el archivo completo en
el buffer" falle de inmediato.

### 1.2 Los exports completos de cuenta devolvían cero mensajes

**Dónde:** `parser_json.py`
**Severidad:** crítica

El parser solo entendía `{"name": ..., "messages": [...]}`, el formato de un
chat suelto. El export completo de la cuenta —lo que obtienes en *Ajustes →
Exportar datos de Telegram*— anida los chats en
`{"chats": {"list": [...]}}`. Con ese archivo el programa terminaba
correctamente, sin errores, informando de **cero coincidencias**. El usuario
concluiría que no hay nada que encontrar.

**Corrección:** el escáner localiza cada array `messages` esté donde esté, y
recuerda el `name` que lo precede como nombre del chat. Ambos formatos
funcionan con el mismo código.

**Verificación:** `test_export_completo_de_la_cuenta_con_varios_chats`,
`test_soporta_el_export_completo_de_la_cuenta`.

### 1.3 Los resultados se acumulaban entre ejecuciones

**Dónde:** `exportador.py`
**Severidad:** alta

Los `.txt` se abrían en modo `"a"` (añadir). Cada ejecución apilaba sus
resultados sobre los anteriores. Tras editar `patrones.txt` y volver a
ejecutar, el archivo mezclaba coincidencias de ambas versiones sin ninguna
marca que las distinguiera.

**Corrección:** la primera escritura de cada ejecución trunca el archivo. Las
reaperturas posteriores (por desalojo de la caché LRU) sí usan modo añadir,
para no perder lo ya escrito en la misma ejecución.

**Verificación:** `test_cada_ejecucion_reemplaza_los_resultados_anteriores`,
`test_una_segunda_ejecucion_no_duplica_los_resultados`.

---

## 2. Defectos de robustez

### 2.1 Descriptores de archivo sin límite

**Dónde:** `exportador.py`

Se mantenía un archivo abierto por patrón durante toda la ejecución. Con
cientos de patrones se agota el límite del sistema operativo (~512 en
Windows, bastante menos en Android), y el fallo aparece a mitad del proceso,
cuando ya se han invertido minutos de trabajo.

**Corrección:** caché LRU con tope configurable (32 por defecto). Los
archivos usados hace más tiempo se cierran y se reabren si vuelven a hacer
falta. **Verificación:** `test_el_limite_de_archivos_abiertos_no_pierde_contenido`.

### 2.2 Colisiones de nombre que mezclaban resultados

**Dónde:** `exportador.py`

Dos patrones distintos podían sanearse al mismo nombre de archivo (`a/b` y
`a\b` → `a_b.txt`), mezclando sus coincidencias sin aviso alguno.

**Corrección:** las colisiones se detectan y se resuelven con un sufijo
numérico, dejando constancia en el log. **Verificación:**
`test_nombres_distintos_que_colisionan_no_mezclan_resultados`.

### 2.3 Nombres de patrón duplicados

**Dónde:** `regex_loader.py`

Dos líneas con el mismo nombre (`dup :: uno` y `dup :: dos`) escribían en el
mismo archivo. **Corrección:** el segundo se renombra a `dup_<linea>` con un
aviso. **Verificación:** `test_renombra_patrones_con_nombre_duplicado`.

### 2.4 Las carpetas de `config.json` no se creaban

**Dónde:** `main.py`

Se creaban siempre `datos/`, `resultados/`, `logs/` y `cache/`, ignorando las
rutas de `config.json`. Cambiar `carpeta_resultados` a `mis_resultados`
provocaba que esa carpeta nunca se creara.

**Corrección:** las carpetas se crean a partir de la configuración ya
resuelta. **Verificación:** `test_respeta_carpetas_personalizadas_en_config`.

### 2.5 Configuración sin validar

**Dónde:** `config_manager.py`

La función prometía en su docstring devolver configuración "ya validada" y no
validaba nada. Consecuencias reales:

- `"modo_busqueda": "buscar"` (error de tipeo) → ninguna rama del motor
  coincidía → **cero resultados en silencio**.
- `"actualizar_progreso_cada_n_mensajes": "cincuenta"` → `TypeError` al
  arrancar.
- `"codificacion_salida": "utf-999"` → `LookupError` al escribir el primer
  resultado, tras haber procesado ya todo.

**Corrección:** cada clave se valida por tipo y por rango. Un valor inválido
se sustituye por el de por defecto con un aviso explícito en el log, y el
archivo se reescribe con la configuración efectiva. Se pasó de `dict` a
`dataclass` tipado para que los errores de nombre de clave se detecten al
escribir el código. **Verificación:** 15 pruebas en
`tests/test_config_manager.py`.

### 2.6 Un modo de búsqueda desconocido fallaba en silencio

**Dónde:** `search_engine.py`

Las ramas `if/elif` no tenían `else`: un modo no contemplado devolvía cero
coincidencias sin error. **Corrección:** lanza `ValueError`. Un fallo ruidoso
es siempre preferible a un resultado vacío indistinguible de "no hay nada".
**Verificación:** `test_modo_no_soportado_lanza_en_lugar_de_callar`.

### 2.7 `findall` volcaba tuplas de Python en los resultados

**Dónde:** `search_engine.py`

`findall()` devuelve tuplas cuando el patrón tiene varios grupos de captura.
El código hacía `str(tupla)`, escribiendo `('24', '12')` en el archivo de
resultados. **Corrección:** los grupos no vacíos se unen con ` | `.
**Verificación:** `test_findall_con_varios_grupos_no_vuelca_una_tupla`.

### 2.8 Campos ausentes se escribían como el literal `"None"`

**Dónde:** `parser_json.py`

`str(mensaje.get("date"))` produce `"None"` cuando el campo falta. En los
canales, `from` viene a `null`, así que el autor de cada publicación aparecía
como `Usuario: None`.

**Corrección:** los campos ausentes producen cadena vacía, y el remitente
recurre en orden a `from`, `actor`, `from_id` y `actor_id` antes de caer en
`"desconocido"`. **Verificación:**
`test_campos_ausentes_no_producen_el_literal_none`,
`test_remitente_nulo_usa_el_identificador`.

### 2.9 Mensajes no-objeto rompían el parseo

**Dónde:** `parser_json.py`

`mensaje_crudo.get("type")` lanza `AttributeError` si el elemento del array
no es un diccionario. **Corrección:** se comprueba el tipo y se ignora lo que
no sea un objeto. **Verificación:** `test_elementos_no_objeto_se_ignoran`.

### 2.10 `a::b` se partía por error

**Dónde:** `regex_loader.py`

Cualquier `::` se interpretaba como separador de nombre, así que el patrón
legítimo `a{2}::b` se rompía en nombre `a{2}` y regex `b`. **Corrección:**
solo se trata como nombre si la parte izquierda no contiene metacaracteres de
expresión regular. **Verificación:**
`test_no_parte_un_regex_que_contiene_dos_puntos_dobles`.

---

## 3. Defectos de interfaz

### 3.1 Los logs pisaban la barra de progreso

**Dónde:** `logger_setup.py` + `progress.py`

La barra escribía en stdout con `\r` mientras los logs iban a stderr. En una
consola normal ambos flujos van a la misma pantalla: cada mensaje de log
partía la línea de progreso y dejaba fragmentos por todas partes.

**Corrección:** el handler de consola borra la línea de progreso antes de
escribir, mediante un limpiador que la barra registra al activarse.

### 3.2 Ancho fijo de 120 columnas

**Dónde:** `progress.py`

`linea.ljust(120)` en una terminal de 60 columnas —lo normal en Pydroid 3—
provoca salto automático de línea, y con `\r` el resultado son cientos de
líneas basura en lugar de una actualizándose.

**Corrección:** el ancho se consulta con `shutil.get_terminal_size()` y la
línea se recorta, reservando una columna (escribir en la última fuerza salto
en algunas consolas de Windows). **Verificación:**
`test_la_linea_nunca_excede_el_ancho_de_la_terminal`.

### 3.3 Basura al redirigir la salida

**Dónde:** `progress.py`

Sin detección de terminal, `python main.py > salida.txt` llenaba el archivo
de retornos de carro. **Corrección:** la barra se desactiva sola si la salida
no es interactiva. **Verificación:**
`test_se_desactiva_cuando_la_salida_no_es_terminal`.

### 3.4 `UnicodeEncodeError` en consolas de Windows

**Dónde:** `logger_setup.py`

Un nombre de chat con emojis reventaba el handler de consola en cp1252.
**Corrección:** el formateador sustituye los caracteres no representables en
lugar de propagar la excepción.

### 3.5 Repintado excesivo

**Dónde:** `progress.py`

Refrescar cada N mensajes sin control de tiempo llega a costar más que la
propia búsqueda. **Corrección:** intervalo mínimo de 0,15 s entre repintados.

### 3.6 Errores de consola tumbaban el análisis

**Dónde:** `progress.py`

`python main.py | head` provoca `BrokenPipeError` al escribir la barra.
**Corrección:** los errores de escritura desactivan la barra en lugar de
propagarse. **Verificación:** `test_una_salida_cerrada_no_propaga_el_error`.

---

## 4. Seguridad

### 4.1 Escritura fuera de la carpeta de resultados

**Dónde:** `filesystem.py`

`sanear_nombre_archivo` no neutralizaba `..`, así que un patrón llamado
`../../config` escribiría fuera de `resultados/`. Se activa con el propio
archivo de patrones del usuario, no con datos externos, pero es un fallo de
saneado que no cuesta nada corregir.

**Corrección:** se eliminan los puntos y espacios de los extremos —lo que
neutraliza `..`— además de sustituir los separadores de ruta. Se esquivan
también los nombres reservados de Windows (`CON`, `NUL`, `COM1`...), que
provocan errores de E/S inexplicables. **Verificación:**
`test_neutraliza_los_saltos_de_directorio`,
`test_esquiva_los_nombres_reservados_de_windows`,
`test_no_escapa_de_la_carpeta_de_resultados`.

### 4.2 Sin protección frente a retroceso catastrófico

**Dónde:** `search_engine.py`

Un patrón como `(a+)+$` tarda un tiempo exponencial sobre ciertas entradas.
El programa se quedaba colgado sin explicación y el usuario no tenía forma de
saber qué patrón era el culpable.

**Corrección:** se mide el tiempo de cada evaluación y se avisa una vez por
patrón cuando supera medio segundo, nombrando el patrón y su línea. También
se acumula el tiempo total por patrón para el resumen final.

No se impone un límite duro: `signal.alarm` no existe en Windows y usar hilos
para cada evaluación costaría más que la búsqueda. Un diagnóstico claro es
mejor que una protección que solo funciona en la mitad de las plataformas.

### 4.3 Credenciales

Las credenciales de la API se leen de variables de entorno o de
`credenciales.json` (que está en `.gitignore`), **nunca del código fuente**.
El `api_hash` jamás se escribe entero en los logs: siempre enmascarado
(`8eb5...4b2f`). Los archivos `.session`, que dan acceso a la cuenta, también
están excluidos del repositorio. **Verificación:** 6 pruebas en
`TestCredenciales`.

### 4.4 Inyección de opciones en pip

**Dónde:** `utils/dependencias.py` (módulo nuevo)

Al añadir la instalación automática de dependencias, un nombre de paquete
como `--upgrade` se interpretaría como opción de pip. **Corrección:** los
nombres se validan contra una expresión regular estricta y se pasan tras
`--`. Además, la instalación nunca ocurre por defecto: hay que pedirla
explícitamente con `--instalar-dependencias`. **Verificación:**
`test_rechaza_nombres_de_paquete_peligrosos`.

---

## 4.bis Defectos detectados en el propio módulo de descarga

Al ampliar el proyecto para recorrer todos los grupos y canales de una
cuenta, la primera versión del descargador arrastraba dos defectos que la
revisión detectó antes de darla por buena.

### 4.bis.1 Los mensajes se acumulaban en memoria

**Dónde:** `core/descargador.py`

`descargar_chat` construía la lista completa de mensajes antes de escribir
nada. Para un chat suelto es irrelevante; para *todos* los grupos y canales
de una cuenta son potencialmente millones de mensajes en RAM, reproduciendo
exactamente el defecto 1.1 que este proyecto se propuso corregir.

**Corrección:** los mensajes se serializan a disco según llegan. El pico de
memoria de una descarga ya no depende del tamaño del historial.

### 4.bis.2 Sin manejo de los límites de peticiones

**Dónde:** `core/descargador.py`

Recorrer todos los diálogos dispara `FloodWaitError` con total seguridad: el
servidor obliga a esperar. Sin tratarlo, la descarga moría a mitad y dejaba
el trabajo a medias.

**Corrección:** se espera lo que pide Telegram y se reanuda usando el
identificador del último mensaje recibido, sin repetir ni perder ninguno. Si
la espera supera el máximo configurado, se salta ese chat y se continúa.

**Añadido en la misma revisión:** los archivos se escriben con extensión
`.parcial` y se renombran al completarse, para que una descarga interrumpida
—incluida una cancelación con Ctrl+C— nunca deje un JSON truncado en la
carpeta que después analiza el programa.

### 4.bis.3 Los grupos privados no se podían seleccionar

**Dónde:** `core/descargador.py`

La selección de chats usaba `get_entity()`, que resuelve identificadores
numéricos y nombres de usuario. Los grupos privados **no tienen nombre de
usuario**, así que la única forma de elegirlos era conocer su ID numérico,
que no aparece en ninguna parte de la interfaz de Telegram.

**Corrección:** se recorre la lista de diálogos y se emparejan por ID, por
`@usuario` o por parte del nombre, sin distinguir mayúsculas. Se añade
además `--listar-chats`, que muestra los chats accesibles con su tipo, ID y
nombre. **Verificación:** `TestSeleccionDeChats`, 11 pruebas.

Un detalle que merece mención: Telegram modela los supergrupos como canales,
de modo que `is_channel` es cierto tanto para un canal de difusión como para
un supergrupo. Filtrar por "canales" sin excluir los grupos habría devuelto
resultados que ningún usuario esperaría. **Verificación:**
`test_tipo_canales_excluye_los_supergrupos`.

---

## 4.ter Filtrado por rango de fechas

Añadido a petición del usuario, con dos decisiones que conviene justificar.

**Un solo recorrido para todas las ventanas.** Las ventanas pedidas (30, 60,
90, 160 y 180 días) son concéntricas: lo que cae en 30 cae también en 60.
Procesar cinco veces el historial habría multiplicado por cinco el tiempo
para obtener información que se puede repartir en una sola pasada. Cada
coincidencia se escribe en todas las ventanas que la contienen, y se
descartan de entrada los mensajes que no caben ni en la más amplia, de modo
que ni siquiera se evalúan contra los patrones.

**Los mensajes con fecha ilegible se incluyen, no se descartan.** Un filtro
de fechas que se traga coincidencias reales por un formato inesperado es
peor que inútil en una herramienta de análisis: da una respuesta incompleta
con apariencia de completa. Se incluyen y se avisa en el log de cuántos
fueron. **Verificación:** `test_las_fechas_ilegibles_se_incluyen`.

El parseo cubre los dos formatos que emite Telegram —el ISO del export JSON
y el `DD.MM.AAAA HH:MM:SS UTC+HH:MM` del HTML—, además de marcas de tiempo
Unix. Todo se normaliza a la hora local que muestra la aplicación, para que
un mismo mensaje no caiga a un lado u otro del corte según de qué formato
provenga. **Verificación:** `tests/test_filtro_fechas.py`, 32 pruebas.

---

## 5. Mantenibilidad

### 5.1 Dependencia cruzada entre capas

`io_utils/exportador.py` importaba de `core/search_engine.py` solo para
conocer la clase `Coincidencia`, obligando a la capa de salida a depender del
motor de búsqueda.

**Corrección:** las tres estructuras compartidas (`Mensaje`, `PatronRegex`,
`Coincidencia`) viven ahora en `core/models.py`. Cada capa depende de los
datos, no de la lógica de otra capa.

### 5.2 Duplicación de contadores

`main.py` llamaba a `registrar_evaluacion_regex()` una vez por patrón y por
mensaje: con 150.000 mensajes y 10 patrones son 1,5 millones de llamadas a
función solo para incrementar un entero. **Corrección:**
`registrar_evaluaciones_regex(n)` incrementa en bloque.

### 5.3 Logs perdidos al arrancar

La carpeta de logs se decide leyendo `config.json`, así que los avisos
generados por esa lectura se emitían antes de que existieran los archivos de
log y se perdían. Justo los avisos que explican por qué la configuración no
es la esperada.

**Corrección:** un handler temporal retiene los registros iniciales y los
vuelca en cuanto los archivos definitivos están listos.

### 5.4 Handlers de logging sin cerrar

`handlers.clear()` eliminaba los handlers sin cerrarlos, dejando descriptores
abiertos y bloqueando los archivos de log en Windows. **Corrección:** se
cierran explícitamente, y hay una función `cerrar_logging()` para el final
del programa.

---

## 6. Funcionalidad completada

Además de corregir defectos, se completaron piezas que estaban pendientes:

- **Parser HTML** (`core/parser_html.py`): antes era un stub vacío. Ahora
  procesa los exports HTML de Telegram alimentando `html.parser` por bloques,
  incluyendo mensajes `joined` (que heredan el remitente del anterior),
  entidades HTML, `<br>` y el nombre del chat de la cabecera. 11 pruebas.
- **Verificación de dependencias** (`utils/dependencias.py`): comprueba la
  versión de Python y la presencia de los módulos estándar usados —algunas
  distribuciones de Python para Android vienen recortadas—, e instala los
  paquetes opcionales bajo petición explícita.
- **Descarga en vivo** (`core/descargador.py`): módulo opcional que trae
  historiales por MTProto y los guarda con el formato de export, de modo que
  el pipeline los procese sin adaptadores.
- **Suite de pruebas**: no existía ninguna. Ahora hay 145.

---

## 7. Alcance de la verificación

Conviene ser explícito sobre qué está probado y qué no.

**Verificado por pruebas automatizadas:** el pipeline completo de extremo a
extremo (JSON y HTML), el lector incremental con bloques diminutos, los tres
modos de búsqueda, la deduplicación, la exportación con sus casos límite, la
validación de la configuración, el saneado de nombres, los contadores de
progreso y la conversión de mensajes del descargador.

**Verificado por medición:** el consumo de memoria (48,6 MB de entrada →
1,26 MB de pico), con recuento de mensajes idéntico al del método anterior.

**Verificado en dispositivo:** ejecución real en Pydroid 3 sobre Android, con
el proyecto en `/storage/emulated/0/QPYTHON/TelegramRegexSearch`. Arranca sin
instalar ningún paquete, crea `config.json` y las cuatro carpetas, resuelve
las rutas de Android, carga y compila los patrones, escribe ambos archivos de
log y termina con un código de salida limpio. Con la carpeta de datos vacía
informa del problema con precisión en lugar de fallar de forma oscura, que es
el comportamiento buscado.

**No verificado — y por qué:**

- **La conexión real por MTProto.** Autenticarse requiere el código que
  Telegram envía al teléfono del titular de la cuenta, que no es algo que
  pueda completarse de forma automatizada. Lo que sí está probado es la
  frontera que importa: que el archivo generado por el descargador lo lee el
  parser del proyecto sin ningún caso especial
  (`test_el_archivo_generado_lo_lee_el_parser_del_proyecto`). Las llamadas a
  Telethon están escritas contra su API documentada, pero no ejecutadas
  contra el servidor.
- **Exports de Telegram reales.** Las pruebas usan exports sintéticos
  construidos según el formato documentado, incluyendo los dos casos que
  fallaban antes (chat suelto y cuenta completa).
- **Rendimiento en Android con un historial grande.** La medición de memoria
  se hizo sobre CPython 3.11 en Linux; solo puede confirmarse en el
  dispositivo con datos reales del usuario.

---

## 8. Resumen

| Área | Antes | Ahora |
|---|---|---|
| Memoria (export de 48 MB) | 148 MB | 1,26 MB |
| Formatos de export JSON | 1 de 2 | 2 de 2 |
| Formato HTML | Sin implementar | Implementado |
| Resultados entre ejecuciones | Se acumulaban | Se reemplazan |
| Validación de configuración | Ninguna | Todas las claves |
| Límite de descriptores | Ninguno | Caché LRU |
| Selección de grupos privados | Imposible sin el ID | Por nombre, ID o @usuario |
| Filtrado por fechas | No existía | Ventanas múltiples en una pasada |
| Pruebas automatizadas | 0 | 197 |
| Líneas > 100 caracteres | Varias | 0 |
