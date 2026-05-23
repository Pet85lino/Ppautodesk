using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Civil3DConnector.AI;
using Civil3DConnector.Analyzers;
using Civil3DConnector.Generators;
using Civil3DConnector.Integration;
using Civil3DConnector.Models;
using Civil3DConnector.Validators;

[assembly: CommandClass(typeof(Civil3DConnector.Commands.Civil3DCommands))]

namespace Civil3DConnector.Commands
{
    /// <summary>
    /// AutoCAD command entry points for the Civil 3D Intelligent Connector.
    /// All commands are accessible from the AutoCAD command line and ribbon.
    /// </summary>
    public class Civil3DCommands
    {
        // ─────────────────────────────────────────────────────────────────────
        // Analyzer Commands
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Analyzes the current DWG for all Civil 3D issues.</summary>
        [CommandMethod("CIVILANALYZE", CommandFlags.Modal)]
        public void AnalyzeDwg()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc.Editor;

            ed.WriteMessage("\n[Civil3D Connector] Iniciando análisis completo del DWG...\n");

            try
            {
                // Progress reporter that echoes to the AutoCAD command line.
                var progress = new Progress<AnalysisProgress>(p =>
                    ed.WriteMessage($"\n  [{p.PercentComplete,3}%] {p.Phase}: {p.Message}"));

                var analyzer = new DwgAnalyzer(AnalyzerOptions.Default, progress);

                // AnalyzeAsync requires an open Database; run synchronously via GetAwaiter.
                var result = analyzer.AnalyzeAsync(doc.Database).GetAwaiter().GetResult();
                var summary = result.Summary!;

                ed.WriteMessage($"\n\n=== RESULTADO DEL ANÁLISIS ===");
                ed.WriteMessage($"\nPuntuación de salud:  {summary.HealthScore}/100 ({summary.HealthTier})");
                ed.WriteMessage($"\nObjetos inventariados:{result.ObjectInventory.Count}");
                ed.WriteMessage($"\nProblemas críticos:   {summary.CriticalCount}");
                ed.WriteMessage($"\nAdvertencias:         {summary.WarningCount}");
                ed.WriteMessage($"\nInformación:          {summary.InfoCount}");
                ed.WriteMessage($"\nDuración:             {result.Duration.TotalSeconds:F1}s");

                if (summary.CriticalCount > 0)
                {
                    ed.WriteMessage("\n\n--- PROBLEMAS CRÍTICOS ---");
                    foreach (var issue in result.CriticalIssues)
                        ed.WriteMessage($"\n  [{issue.Category}] {issue.ObjectType} '{issue.ObjectName}': {issue.Message}");
                }

                var psr = ed.GetString(new PromptStringOptions("\nGuardar reportes (HTML+JSON+CSV)? (S/N) [S]: ")
                    { AllowSpaces = false });

                if (psr.Status == PromptStatus.OK && !string.Equals(psr.StringResult, "N",
                                                          StringComparison.OrdinalIgnoreCase))
                {
                    string outputDir = Path.GetDirectoryName(doc.Name) ?? Path.GetTempPath();
                    var reportGen = new ReportGenerator();
                    var paths = reportGen.WriteAllReports(result, outputDir, ReportOptions.Default);

                    ed.WriteMessage($"\nReportes guardados en: {outputDir}");
                    foreach (var kv in paths)
                        ed.WriteMessage($"\n  {kv.Key}: {Path.GetFileName(kv.Value)}");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ERROR] {ex.Message}");
            }
        }

        /// <summary>Validates the drawing against Ecuadorian engineering standards.</summary>
        [CommandMethod("CIVILVALIDAR", CommandFlags.Modal)]
        public void ValidateEcuadorianStandards()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            ed.WriteMessage("\n[Civil3D Connector] Validando normas ecuatorianas...\n");

            var kwOpts = new PromptKeywordOptions("\nSeleccionar normativa [Interagua/Amagua/MTOP/NEC/Todas]: ");
            kwOpts.Keywords.Add("Interagua");
            kwOpts.Keywords.Add("Amagua");
            kwOpts.Keywords.Add("MTOP");
            kwOpts.Keywords.Add("NEC");
            kwOpts.Keywords.Add("Todas");
            kwOpts.Keywords.Default = "Todas";

            var psr = ed.GetKeywords(kwOpts);
            if (psr.Status != PromptStatus.OK) return;

            try
            {
                using var validator = new EcuadorianStandardsValidator(doc);
                var standard = psr.StringResult switch
                {
                    "Interagua" => ValidatorStandard.Interagua,
                    "Amagua"    => ValidatorStandard.Amagua,
                    "MTOP"      => ValidatorStandard.MTOP,
                    "NEC"       => ValidatorStandard.NEC,
                    _           => ValidatorStandard.All
                };

                var violations = validator.ValidateAll(standard);

                ed.WriteMessage($"\n=== VALIDACIÓN NORMATIVA ({psr.StringResult}) ===");
                ed.WriteMessage($"\nTotal violaciones: {violations.Count}");

                foreach (var v in violations)
                    ed.WriteMessage($"\n  [{v.Standard}] {v.ObjectName}: {v.ViolationMessage} " +
                                    $"(Valor={v.ActualValue:F2}, Límite={v.LimitValue:F2} {v.Unit})");

                if (violations.Count == 0)
                    ed.WriteMessage("\n  ✓ Sin violaciones encontradas.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ERROR] {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Dynamo Generator Commands
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Generates a Dynamo script for the selected Civil 3D workflow.</summary>
        [CommandMethod("CIVILDYNAMO", CommandFlags.Modal)]
        public void GenerateDynamoScript()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            var kwOpts = new PromptKeywordOptions(
                "\nSeleccionar workflow Dynamo [Corredor/RedTuberias/Superficie/Alineamiento/Perfil/Personalizado]: ");
            kwOpts.Keywords.Add("Corredor");
            kwOpts.Keywords.Add("RedTuberias");
            kwOpts.Keywords.Add("Superficie");
            kwOpts.Keywords.Add("Alineamiento");
            kwOpts.Keywords.Add("Perfil");
            kwOpts.Keywords.Add("Personalizado");
            kwOpts.Keywords.Default = "Corredor";

            var kw = ed.GetKeywords(kwOpts);
            if (kw.Status != PromptStatus.OK) return;

            try
            {
                var generator = new DynamoScriptGenerator();
                string outputDir = Path.GetDirectoryName(doc.Name) ?? Path.GetTempPath();
                string scriptName = $"{kw.StringResult}_{DateTime.Now:yyyyMMdd_HHmm}.dyn";
                string outputPath = Path.Combine(outputDir, scriptName);

                string dynJson = kw.StringResult switch
                {
                    "Corredor"    => generator.GenerateCorridorScript(),
                    "RedTuberias" => generator.GeneratePipeNetworkScript(),
                    "Superficie"  => generator.GenerateSurfaceScript(),
                    "Alineamiento"=> generator.GenerateAlignmentScript(),
                    "Perfil"      => generator.GenerateProfileScript(),
                    _             => generator.GenerateCustomScript(PromptForCustomWorkflow(ed))
                };

                File.WriteAllText(outputPath, dynJson, System.Text.Encoding.UTF8);
                ed.WriteMessage($"\n[Civil3D Connector] Script Dynamo generado: {outputPath}");
                ed.WriteMessage($"\n  Abrir Dynamo en Civil 3D y cargar el archivo para ejecutarlo.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ERROR] {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Plugin Generator Commands
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Generates a complete .NET plugin project for Civil 3D.</summary>
        [CommandMethod("CIVILPLUGIN", CommandFlags.Modal)]
        public void GenerateDotNetPlugin()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            var nameOpts = new PromptStringOptions("\nNombre del plugin: ") { AllowSpaces = false };
            var nameResult = ed.GetString(nameOpts);
            if (nameResult.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(nameResult.StringResult)) return;

            var descOpts = new PromptStringOptions("\nDescripción del plugin: ") { AllowSpaces = true };
            var descResult = ed.GetString(descOpts);

            try
            {
                var generator = new DotNetPluginGenerator();
                string outputDir = Path.Combine(
                    Path.GetDirectoryName(doc.Name) ?? Path.GetTempPath(),
                    nameResult.StringResult);

                var options = new PluginGeneratorOptions
                {
                    PluginName = nameResult.StringResult,
                    Description = descResult.Status == PromptStatus.OK ? descResult.StringResult : "",
                    TargetVersions = new[] { "2025", "2026", "2027" },
                    IncludeRibbon = true,
                    IncludePalette = true,
                    IncludeEventHandlers = true,
                    OutputDirectory = outputDir
                };

                generator.GenerateProject(options);
                ed.WriteMessage($"\n[Civil3D Connector] Proyecto plugin generado en: {outputDir}");
                ed.WriteMessage($"\n  Abrir {nameResult.StringResult}.sln en Visual Studio para compilar.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ERROR] {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // AI / Natural Language Command
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Processes a natural language command for Civil 3D automation.</summary>
        [CommandMethod("CIVILAI", CommandFlags.Modal)]
        public void ProcessAiCommand()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            ed.WriteMessage("\n[Civil3D AI] Asistente inteligente activo.");
            ed.WriteMessage("\n  Ejemplos: 'crear corredor de 500m', 'validar pendientes Interagua', 'analizar red sanitaria'\n");

            var opts = new PromptStringOptions("\nIngrese instrucción en español o inglés: ") { AllowSpaces = true };
            var result = ed.GetString(opts);
            if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult)) return;

            try
            {
                var nlp = new NaturalLanguageProcessor();
                var plan = nlp.Parse(result.StringResult);

                ed.WriteMessage($"\n[AI] Interpretación: {plan.IntentDescription}");
                ed.WriteMessage($"\n[AI] Confianza: {plan.Confidence:P0}");

                if (plan.Confidence < 0.3)
                {
                    ed.WriteMessage("\n[AI] Instrucción no reconocida. Use CIVILAYUDA para ver comandos disponibles.");
                    return;
                }

                ed.WriteMessage($"\n[AI] Workflow: {plan.Steps.Count} pasos");
                foreach (var step in plan.Steps)
                    ed.WriteMessage($"\n  {step.Order}. {step.Description}");

                var confirm = ed.GetKeywords(new PromptKeywordOptions("\nEjecutar? [Si/No]: ")
                {
                    Keywords = { "Si", "No" },
                    AllowNone = false
                });

                if (confirm.Status == PromptStatus.OK && confirm.StringResult == "Si")
                {
                    var wfGen = new WorkflowGenerator(doc);
                    wfGen.Execute(plan);
                    ed.WriteMessage("\n[AI] Workflow ejecutado con éxito.");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ERROR] {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GIS Export Command
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Exports Civil 3D objects to GeoJSON.</summary>
        [CommandMethod("CIVILEXPORTGIS", CommandFlags.Modal)]
        public void ExportToGis()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            string defaultPath = Path.Combine(
                Path.GetDirectoryName(doc.Name) ?? Path.GetTempPath(),
                Path.GetFileNameWithoutExtension(doc.Name) + "_export.geojson");

            var pathOpts = new PromptStringOptions($"\nRuta de salida [{defaultPath}]: ") { AllowSpaces = true };
            var pathResult = ed.GetString(pathOpts);
            string outputPath = pathResult.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(pathResult.StringResult)
                ? pathResult.StringResult
                : defaultPath;

            try
            {
                using var connector = new GisConnector(doc);
                connector.ExportToGeoJson(outputPath, new GisExportOptions());
                ed.WriteMessage($"\n[Civil3D Connector] Exportado GeoJSON: {outputPath}");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ERROR] {ex.Message}");
            }
        }

        /// <summary>Exports to LandXML format.</summary>
        [CommandMethod("CIVILLANDXML", CommandFlags.Modal)]
        public void ExportToLandXml()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc.Editor;

            string defaultPath = Path.Combine(
                Path.GetDirectoryName(doc.Name) ?? Path.GetTempPath(),
                Path.GetFileNameWithoutExtension(doc.Name) + ".xml");

            var pathOpts = new PromptStringOptions($"\nRuta LandXML [{defaultPath}]: ") { AllowSpaces = true };
            var pathResult = ed.GetString(pathOpts);
            string outputPath = (pathResult.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(pathResult.StringResult))
                ? pathResult.StringResult : defaultPath;

            try
            {
                using var connector = new GisConnector(doc);
                connector.ExportToLandXml(outputPath, new LandXmlExportOptions());
                ed.WriteMessage($"\n[Civil3D Connector] LandXML exportado: {outputPath}");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ERROR] {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Help Command
        // ─────────────────────────────────────────────────────────────────────

        [CommandMethod("CIVILAYUDA", CommandFlags.Modal)]
        public void ShowHelp()
        {
            var ed = Application.DocumentManager.MdiActiveDocument.Editor;
            ed.WriteMessage(@"
╔════════════════════════════════════════════════════════════════════════╗
║           CIVIL 3D INTELLIGENT CONNECTOR - COMANDOS DISPONIBLES        ║
╠════════════════════════════════════════════════════════════════════════╣
║  ANÁLISIS                                                              ║
║    CIVILANALYZE   - Análisis completo del DWG                          ║
║    CIVILVALIDAR   - Validar normas ecuatorianas (Interagua/MTOP/NEC)   ║
║                                                                        ║
║  GENERACIÓN DE CÓDIGO                                                  ║
║    CIVILDYNAMO    - Generar script Dynamo para Civil 3D                 ║
║    CIVILPLUGIN    - Generar proyecto plugin .NET                        ║
║                                                                        ║
║  EXPORTACIÓN / INTEGRACIÓN                                             ║
║    CIVILEXPORTGIS - Exportar a GeoJSON                                 ║
║    CIVILLANDXML   - Exportar a LandXML 1.2                             ║
║                                                                        ║
║  INTELIGENCIA ARTIFICIAL                                               ║
║    CIVILAI        - Asistente IA (lenguaje natural)                    ║
║                                                                        ║
║  AYUDA                                                                 ║
║    CIVILAYUDA     - Mostrar esta ayuda                                 ║
╠════════════════════════════════════════════════════════════════════════╣
║  Versión: 2.0  |  Compatible: Civil 3D 2025/2026/2027                 ║
╚════════════════════════════════════════════════════════════════════════╝
");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private string PromptForCustomWorkflow(Editor ed)
        {
            var opts = new PromptStringOptions("\nDescribir workflow personalizado: ") { AllowSpaces = true };
            var result = ed.GetString(opts);
            return result.Status == PromptStatus.OK ? result.StringResult : "";
        }
    }
}
