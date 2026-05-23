# Civil 3D Intelligent Connector v2.0
### Copiloto técnico inteligente para Autodesk Civil 3D 2025 / 2026 / 2027 — Ecuador

---

## ¿Qué es?

Un **plugin profesional para Autodesk Civil 3D** que actúa como copiloto de ingeniería civil. Automatiza tareas repetitivas, genera código Dynamo y C#, valida proyectos contra normativa ecuatoriana y proporciona un asistente de lenguaje natural para Civil 3D 2025, 2026 y 2027.

---

## Capacidades principales

| Módulo | Descripción |
|--------|-------------|
| **Analizador DWG** | Escaneo completo: corrupción, referencias rotas, superficies dañadas, redes inválidas, etiquetas rotas |
| **Generador Dynamo** | Scripts `.dyn` completos para corredor, redes, superficies, alineamientos y perfiles |
| **Generador Plugins .NET** | Proyectos C# compilables con comandos, paletas, ribbon y eventos |
| **Validador Normativo** | Interagua, Amagua, MTOP, NEC, NTE INEN — pendientes, velocidades, coberturas, diámetros, radios |
| **Motor IA** | Lenguaje natural español/inglés → workflow Civil 3D ejecutable |
| **Integración GIS** | GeoJSON ↔ Civil 3D, LandXML 1.2, SHP (via NTS) |
| **Integración BIM** | IFC 2x3/4, Navisworks clash import, InfraWorks IMX |
| **Scripts Python** | CPython 3 para Dynamo: alineamientos, superficies, corredores, redes, cómputos |
| **Base de datos** | SQLite local por proyecto: historial de análisis, violaciones, workflows |

---

## Compatibilidad

| Civil 3D | .NET | AutoCAD | Windows |
|----------|------|---------|---------|
| 2025 | .NET Framework 4.8 | R24 | 10/11 |
| 2026 | .NET 8 | R25 | 10/11 |
| 2027 | .NET 8 | R26 | 10/11 |

---

## Estructura del proyecto

```
Civil3DConnector.sln
├── src/
│   ├── Civil3DConnector/
│   │   ├── Core/
│   │   │   ├── VersionDetector.cs        ← Detección automática 2025/2026/2027
│   │   │   ├── ApiVersionAdapter.cs      ← Adaptación dinámica de APIs
│   │   │   ├── ConnectorBase.cs          ← Base con logging y ciclo de vida
│   │   │   ├── TransactionManager.cs     ← Manejo seguro de transacciones
│   │   │   └── ConnectorApp.cs           ← Punto de entrada IExtensionApplication
│   │   ├── Analyzers/
│   │   │   ├── DwgAnalyzer.cs            ← 13 fases de análisis async
│   │   │   ├── ObjectValidator.cs        ← Validador por tipo de objeto
│   │   │   └── ReportGenerator.cs        ← HTML, JSON, XML, CSV
│   │   ├── Generators/
│   │   │   ├── DynamoScriptGenerator.cs  ← Genera archivos .dyn válidos
│   │   │   ├── PythonDynamoGenerator.cs  ← Scripts CPython para nodos Dynamo
│   │   │   ├── DotNetPluginGenerator.cs  ← Proyectos C# compilables
│   │   │   └── Templates/DynamoTemplates.cs
│   │   ├── Validators/
│   │   │   ├── EcuadorianStandardsValidator.cs
│   │   │   └── StandardsConfig.cs
│   │   ├── Objects/
│   │   │   ├── AlignmentHandler.cs
│   │   │   ├── CorridorHandler.cs        ← Creación, extracción, cómputos
│   │   │   ├── PipeNetworkHandler.cs
│   │   │   └── SurfaceHandler.cs
│   │   ├── Integration/
│   │   │   ├── GisConnector.cs           ← GeoJSON ↔ Civil 3D, LandXML
│   │   │   ├── RevitNavisworksConnector.cs ← IFC, NWC, InfraWorks
│   │   │   └── SqliteDataStore.cs
│   │   ├── AI/
│   │   │   ├── NaturalLanguageProcessor.cs ← NLP español/inglés
│   │   │   └── WorkflowGenerator.cs
│   │   ├── Commands/Civil3DCommands.cs    ← Comandos AutoCAD
│   │   ├── UI/ConnectorPalette.cs         ← Paleta WinForms integrada
│   │   ├── Models/
│   │   │   ├── AnalysisResult.cs
│   │   │   └── WorkflowModels.cs
│   │   └── Standards/
│   │       ├── interagua_standards.json
│   │       ├── amagua_standards.json
│   │       ├── mtop_road_standards.json
│   │       └── nec_standards.json
│   ├── Python/
│   │   ├── civil3d_helpers.py             ← Librería base CPython
│   │   ├── alignment_geometry.py          ← Geometría de alineamientos
│   │   ├── surface_operations.py          ← Operaciones en superficies
│   │   ├── pipe_network_analysis.py       ← Análisis de redes
│   │   └── corridor_quantities.py         ← Cómputos métricos + diagrama de masas
│   └── DynamoScripts/
│       ├── CreateCorridorFromAlignment.dyn
│       └── PipeNetworkValidator.dyn       ← Valida redes contra normas EC
├── tests/Civil3DConnector.Tests/
│   ├── StandardsValidatorTests.cs         ← Tests normas sin dep. AutoCAD
│   ├── NaturalLanguageProcessorTests.cs
│   └── DynamoScriptGeneratorTests.cs
├── scripts/
│   ├── build.ps1                          ← Compilación multi-versión
│   └── install.ps1                        ← Instalación + registro autoload
└── config/
    └── autoload.reg                       ← Registro manual de autoload
```

---

## Instalación rápida

### Requisitos previos
- Visual Studio 2022 o superior
- .NET 8 SDK + .NET Framework 4.8 Developer Pack
- Autodesk Civil 3D 2025, 2026 o 2027 instalado
- PowerShell 7+ (para scripts de build/install)

### 1. Compilar

```powershell
# Compilar para Civil 3D 2026 en modo Release
.\scripts\build.ps1 -Version 2026 -Configuration Release

# Compilar para todas las versiones
.\scripts\build.ps1 -Version All
```

### 2. Instalar (como Administrador)

```powershell
# Instalar para Civil 3D 2026
.\scripts\install.ps1 -Version 2026

# Desinstalar
.\scripts\install.ps1 -Version 2026 -Uninstall
```

### 3. Cargar manualmente en Civil 3D

```
NETLOAD → seleccionar Civil3DConnector.dll
CIVILAYUDA → ver todos los comandos disponibles
```

---

## Comandos AutoCAD disponibles

| Comando | Descripción |
|---------|-------------|
| `CIVILAYUDA` | Muestra todos los comandos disponibles |
| `CIVILANALYZE` | Análisis completo del DWG (13 fases, reporte HTML/JSON/CSV) |
| `CIVILVALIDAR` | Valida contra normas ecuatorianas (Interagua/Amagua/MTOP/NEC/Todas) |
| `CIVILDYNAMO` | Genera script Dynamo (.dyn) para el workflow seleccionado |
| `CIVILPLUGIN` | Genera proyecto plugin .NET compilable en Visual Studio |
| `CIVILAI` | Asistente IA: lenguaje natural → workflow Civil 3D |
| `CIVILEXPORTGIS` | Exporta objetos a GeoJSON |
| `CIVILLANDXML` | Exporta a LandXML 1.2 |

---

## Uso del Asistente IA (`CIVILAI`)

El motor NLP interpreta comandos en **español e inglés**:

```
crear corredor de 500m en alineamiento CL-Principal
generar red sanitaria según normas Interagua
analizar superficie y detectar picos
validar pendientes de tuberías DN200 Amagua
exportar alineamientos a GeoJSON
calcular volumen de corte y relleno corredor Vía-Norte
generar script dynamo para perfiles
```

---

## Scripts Dynamo incluidos

### `CreateCorridorFromAlignment.dyn`
Crea un corredor completo desde:
- Nombre de alineamiento
- Nombre de perfil
- Nombre de ensamble
- Superficie de plantilla (opcional)

### `PipeNetworkValidator.dyn`
Valida toda la red de tuberías contra:
- **Interagua** (Guayaquil): pendientes mínimas por diámetro, velocidades, coberturas
- **Amagua** (Quito): normas DMQ
- **MTOP**: separación entre redes

Salidas: lista de violaciones detalladas + resumen estadístico.

---

## Scripts Python para Dynamo

```python
# civil3d_helpers.py — helpers base
from civil3d_helpers import get_active_document, get_civil_document

# alignment_geometry.py
from alignment_geometry import sample_alignment_points, extract_alignment_entities

# surface_operations.py
from surface_operations import get_elevation_at_point, calculate_cut_fill

# corridor_quantities.py
from corridor_quantities import calculate_earthwork_volumes, generate_mass_haul_diagram

# pipe_network_analysis.py
from pipe_network_analysis import validate_network, export_to_excel
```

---

## Normativas ecuatorianas soportadas

| Norma | Organismo | Aplicación |
|-------|-----------|------------|
| **Interagua** | EP-EMAPAG / Aguas Guayaquil | Redes de agua y alcantarillado Guayaquil |
| **Amagua / EPMAPS** | Empresa Pública Quito | Redes DMQ, Quito |
| **MTOP** | Ministerio de Transporte | Diseño vial, carreteras, pendientes, radios |
| **NEC-SE** | MIDUVI | Estructural, sísmica, cimentaciones |
| **NTE INEN** | INEN | Materiales de tuberías, ensayos |
| **Ordenanza Municipal** | GAD Quito / Guayaquil / Cuenca | Separaciones mínimas, acometidas |

### Parámetros validados automáticamente
- Pendientes mínimas y máximas por diámetro
- Velocidades hidráulicas (Manning) mínima/máxima
- Cobertura mínima bajo calzada, acera y zona verde
- Diámetros mínimos y tamaños estándar
- Espaciado máximo entre pozos de revisión
- Separación entre redes (agua, alcantarillado, gas, eléctrico)
- Radios de curvatura mínimos por clase de vía
- Pendientes máximas de vía por velocidad de diseño
- Factores de seguridad en estabilidad de taludes

---

## API pública (para integraciones externas)

```csharp
// Analizar un DWG
var analyzer = new DwgAnalyzer(AnalyzerOptions.Default);
var result = await analyzer.AnalyzeAsync(database);
new ReportGenerator().WriteAllReports(result, outputDir);

// Validar normativas
using var validator = new EcuadorianStandardsValidator(document);
var violations = validator.ValidateAll(ValidatorStandard.Interagua);

// Generar script Dynamo
var gen = new DynamoScriptGenerator();
string dynJson = gen.GenerateCorridorScript();
File.WriteAllText("corredor.dyn", dynJson);

// Asistente IA
var nlp = new NaturalLanguageProcessor();
var plan = nlp.Parse("crear corredor en alineamiento VN-001");
new WorkflowGenerator(document).Execute(plan);

// Exportar GeoJSON
using var gis = new GisConnector(document);
gis.ExportToGeoJson("proyecto.geojson", new GisExportOptions());
```

---

## Ejecutar tests

```bash
# Tests unitarios (sin Civil 3D instalado)
dotnet test tests/Civil3DConnector.Tests/ --logger "console;verbosity=detailed"
```

Los tests validan la lógica normativa ecuatoriana y el generador Dynamo sin dependencia de AutoCAD.

---

## Arquitectura de compatibilidad multi-versión

```
VersionDetector (4 estrategias)
    ├── 1. Registro Windows (HKLM + HKCU, 64-bit)
    ├── 2. Escaneo de rutas conocidas de instalación
    ├── 3. Variable de entorno (CIVIL3D_2026_PATH, etc.)
    └── 4. Ensamblados ya cargados (cuando corre dentro de Civil 3D)

ApiVersionAdapter (reflection)
    ├── Mapeo de tipos por versión (ConcurrentDictionary)
    ├── Overrides de nombres de métodos por versión
    └── Carga lazy de ensamblados con caché
```

---

## Contribución

1. Clonar el repositorio
2. Crear rama: `feature/mi-mejora`
3. Compilar: `.\scripts\build.ps1 -Version 2026 -Configuration Debug`
4. Tests: `dotnet test`
5. Pull Request con descripción técnica

---

## Licencia

Proyecto de ingeniería civil para Ecuador. Código generado para uso en proyectos reales bajo Civil 3D 2025–2027.

---

*Civil 3D Intelligent Connector v2.0 — Compatible: Civil 3D 2025 / 2026 / 2027 | Ecuador*
