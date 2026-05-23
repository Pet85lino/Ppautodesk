using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Autodesk.Civil3D.Connector.AI
{
    // ─────────────────────────────────────────────────────────────────────────
    // Domain model types
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>High-level Civil 3D operation intent identified from a command.</summary>
    public enum CivilIntent
    {
        Unknown,
        CreateCorridor,
        CreateAlignment,
        CreateProfile,
        CreatePipeNetwork,
        CreateSanitaryNetwork,
        CreateStormNetwork,
        AnalyzeSurface,
        CreateSurface,
        ValidateSlopes,
        ValidatePipeNetwork,
        CalculateVolumes,
        GenerateQuantities,
        ExportData,
        ApplyStandard
    }

    /// <summary>Supported Civil 3D API operation identifiers.</summary>
    public enum Civil3DApiOperation
    {
        // Alignment
        AlignmentCreate,
        AlignmentGetByName,
        AlignmentSetDesignSpeed,
        // Profile
        ProfileCreate,
        ProfileGetByName,
        ProfileSetGradeBreaks,
        // Corridor
        CorridorCreate,
        CorridorAddBaseline,
        CorridorAddRegion,
        CorridorRebuild,
        // Assembly / Subassembly
        AssemblyCreate,
        AssemblyGetByName,
        SubassemblyAdd,
        // Surface
        SurfaceGetByName,
        SurfaceCreate,
        SurfaceAddContourData,
        SurfaceExtractContours,
        SurfaceSampleElevation,
        TinVolumeSurfaceCreate,
        // Pipe network
        PipeNetworkCreate,
        PipeCreate,
        StructureCreate,
        PipeNetworkGetByName,
        // Labels & styles
        LabelStyleApply,
        // Validation
        SlopeValidate,
        CoverDepthValidate,
        DiameterValidate,
        // Export
        ExportLandXml,
        ExportCsv
    }

    /// <summary>A single parameter bound to an API operation.</summary>
    public sealed class ApiParameter
    {
        public string Name  { get; init; } = string.Empty;
        public object Value { get; init; } = string.Empty;
        public string Unit  { get; init; } = string.Empty;

        public override string ToString() =>
            string.IsNullOrEmpty(Unit) ? $"{Name}={Value}" : $"{Name}={Value} [{Unit}]";
    }

    /// <summary>One API call within a workflow step.</summary>
    public sealed class ApiCall
    {
        public Civil3DApiOperation Operation  { get; init; }
        public IReadOnlyList<ApiParameter> Parameters { get; init; } = Array.Empty<ApiParameter>();
        public string? ReturnVariable         { get; init; }

        public override string ToString() =>
            $"{Operation}({string.Join(", ", Parameters)})"
            + (ReturnVariable is null ? "" : $" → {ReturnVariable}");
    }

    /// <summary>One ordered step in an execution plan.</summary>
    public sealed class WorkflowStep
    {
        public int    StepNumber   { get; init; }
        public string Description  { get; init; } = string.Empty;
        public IReadOnlyList<ApiCall> ApiCalls { get; init; } = Array.Empty<ApiCall>();
        public string? RollbackDescription    { get; init; }
        public bool   IsOptional             { get; init; }
    }

    /// <summary>Structured request object produced by the NLP processor.</summary>
    public sealed class WorkflowRequest
    {
        public CivilIntent Intent            { get; init; }
        public string      OriginalCommand   { get; init; } = string.Empty;
        public string      Language          { get; init; } = "en";
        public IReadOnlyDictionary<string, object> ExtractedParameters { get; init; }
            = new Dictionary<string, object>();
        public double      ConfidenceScore   { get; init; }
        public string?     StandardReference { get; init; }
    }

    /// <summary>Complete execution plan with ordered workflow steps.</summary>
    public sealed class ExecutionPlan
    {
        public WorkflowRequest Request       { get; init; } = new();
        public IReadOnlyList<WorkflowStep> Steps { get; init; } = Array.Empty<WorkflowStep>();
        public string    Summary             { get; init; } = string.Empty;
        public string?   Warning             { get; init; }
        public bool      IsValid             => Steps.Count > 0 && Request.ConfidenceScore >= 0.5;
        public TimeSpan  EstimatedDuration   { get; init; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NLP pattern definitions
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Internal pattern record used for intent matching.</summary>
    internal sealed class IntentPattern
    {
        public CivilIntent Intent   { get; init; }
        public Regex[]     Patterns { get; init; } = Array.Empty<Regex>();
        public double      BaseConfidence { get; init; } = 0.8;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NaturalLanguageProcessor
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses natural language commands (Spanish/English) for Civil 3D operations,
    /// extracts parameters, maps intent to Civil 3D API calls, and produces a
    /// structured <see cref="ExecutionPlan"/>.
    /// </summary>
    /// <remarks>
    /// Supported example commands:
    /// <list type="bullet">
    ///   <item>"crear corredor de 500m"</item>
    ///   <item>"generar red sanitaria"</item>
    ///   <item>"analizar superficie"</item>
    ///   <item>"validar pendientes según Interagua"</item>
    ///   <item>"create corridor from alignment EJE-01"</item>
    ///   <item>"analyze surface Existing Ground"</item>
    ///   <item>"validate pipe network against INEN standards"</item>
    /// </list>
    /// </remarks>
    public sealed class NaturalLanguageProcessor
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const RegexOptions RxOpts =
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

        private static readonly Regex RxLength =
            new(@"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>m|km|ft|metros?|meters?|feet|kilometros?)?",
                RxOpts);

        private static readonly Regex RxDiameter =
            new(@"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>mm|cm|m|pulg(?:adas?)?|in(?:ches?)?)",
                RxOpts);

        private static readonly Regex RxSlope =
            new(@"(?<value>\d+(?:[.,]\d+)?)\s*(?:%|por\s*ciento|percent)",
                RxOpts);

        private static readonly Regex RxName =
            new(@"""([^""]+)""|'([^']+)'|(?:llamad[ao]|named?|con\s+nombre)\s+(\S+)",
                RxOpts);

        // Known Ecuadorian standard references
        private static readonly Dictionary<string, string> StandardKeywords =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "interagua",  "INTERAGUA-SDS-2015"  },
                { "inen",       "INEN-2536"            },
                { "epaa",       "EPAA-2020"            },
                { "ex-ieos",    "EX-IEOS-1992"         },
                { "ieos",       "EX-IEOS-1992"         },
                { "senagua",    "SENAGUA-2012"         },
                { "aastho",     "AASHTO-2018"          },
                { "aashto",     "AASHTO-2018"          },
                { "mtop",       "MTOP-2013"            }
            };

        // ── Intent pattern catalog ─────────────────────────────────────────────

        private static readonly IReadOnlyList<IntentPattern> IntentPatterns =
            new List<IntentPattern>
            {
                new()
                {
                    Intent  = CivilIntent.CreateCorridor,
                    BaseConfidence = 0.9,
                    Patterns = new[]
                    {
                        new Regex(@"\b(crear?|generar?|nuevo|create|new|build)\b.{0,30}\bcorredor\b",   RxOpts),
                        new Regex(@"\bcorredor\b.{0,30}\b(crear?|generar?|nuevo|create|new)\b",         RxOpts),
                        new Regex(@"\bcreate\b.{0,30}\bcorridor\b",                                     RxOpts),
                        new Regex(@"\bcorridor\b.{0,20}\b(create|from|with)\b",                         RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.CreateAlignment,
                    BaseConfidence = 0.9,
                    Patterns = new[]
                    {
                        new Regex(@"\b(crear?|generar?|create|new)\b.{0,20}\b(alineaci[oó]n|eje|alignment)\b", RxOpts),
                        new Regex(@"\b(alineaci[oó]n|alignment)\b.{0,20}\b(crear?|nueva?|create|new)\b",       RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.CreateProfile,
                    BaseConfidence = 0.85,
                    Patterns = new[]
                    {
                        new Regex(@"\b(crear?|generar?|create|new)\b.{0,20}\b(perfil|profile)\b",  RxOpts),
                        new Regex(@"\b(perfil|profile)\b.{0,20}\b(longitudinal|vertical|rasante)\b", RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.CreateSanitaryNetwork,
                    BaseConfidence = 0.92,
                    Patterns = new[]
                    {
                        new Regex(@"\b(crear?|generar?|create|new)\b.{0,30}\b(red\s+sanitaria|red\s+de\s+alcantarillado|sanitary\s+network)\b", RxOpts),
                        new Regex(@"\b(red\s+sanitaria|alcantarillado\s+sanitario|sanitary\b)",                                                 RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.CreateStormNetwork,
                    BaseConfidence = 0.92,
                    Patterns = new[]
                    {
                        new Regex(@"\b(crear?|generar?|create|new)\b.{0,30}\b(red\s+pluvial|drenaje\s+pluvial|storm\s+(water\s+)?network)\b", RxOpts),
                        new Regex(@"\b(red\s+pluvial|alcantarillado\s+pluvial|storm\s+drain)\b",                                              RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.CreatePipeNetwork,
                    BaseConfidence = 0.85,
                    Patterns = new[]
                    {
                        new Regex(@"\b(crear?|generar?|create|new)\b.{0,30}\b(red\s+de\s+tuber[ií]as?|pipe\s+network|red\s+de\s+calder[ií]a)\b", RxOpts),
                        new Regex(@"\b(pipe\s+network|red\s+hidr[aá]ulica)\b",                                                                    RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.AnalyzeSurface,
                    BaseConfidence = 0.88,
                    Patterns = new[]
                    {
                        new Regex(@"\b(analizar?|analyze?|an[aá]lisis)\b.{0,30}\b(superficie|surface|terreno|terrain|dtm|dem)\b", RxOpts),
                        new Regex(@"\b(superficie|surface|terreno)\b.{0,20}\b(analizar?|analyze?)\b",                             RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.CreateSurface,
                    BaseConfidence = 0.88,
                    Patterns = new[]
                    {
                        new Regex(@"\b(crear?|generar?|create|new)\b.{0,20}\b(superficie|surface|tin|terreno)\b", RxOpts),
                        new Regex(@"\b(superficie|surface)\b.{0,20}\b(nueva?|crear?|new|create)\b",               RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.ValidateSlopes,
                    BaseConfidence = 0.90,
                    Patterns = new[]
                    {
                        new Regex(@"\b(validar?|verificar?|comprobar?|validate?|check)\b.{0,30}\b(pendientes?|slopes?|gradientes?|grades?)\b", RxOpts),
                        new Regex(@"\b(pendientes?|slopes?)\b.{0,20}\b(m[aá]ximas?|m[ií]nimas?|max|min|l[ií]mite)\b",                        RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.ValidatePipeNetwork,
                    BaseConfidence = 0.90,
                    Patterns = new[]
                    {
                        new Regex(@"\b(validar?|verificar?|comprobar?|validate?|check)\b.{0,30}\b(red\s+de\s+tuber[ií]as?|pipe\s+network|tuber[ií]as?|pipes?)\b", RxOpts),
                        new Regex(@"\b(redes?\s+(sanitaria|pluvial))\b.{0,20}\b(validar?|verificar?|validate?)\b",                                                RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.CalculateVolumes,
                    BaseConfidence = 0.87,
                    Patterns = new[]
                    {
                        new Regex(@"\b(calcular?|calculate?|c[aá]lculo)\b.{0,30}\b(vol[uú]menes?|volumes?|corte|relleno|cut|fill)\b", RxOpts),
                        new Regex(@"\b(vol[uú]menes?\s+de\s+(movimiento|tierra|corte|relleno))\b",                                    RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.GenerateQuantities,
                    BaseConfidence = 0.85,
                    Patterns = new[]
                    {
                        new Regex(@"\b(generar?|generate?|calcular?|calculate?)\b.{0,30}\b(cantidades?|quantities|cubicaci[oó]n|metrado)\b", RxOpts),
                        new Regex(@"\b(metrados?|takeoffs?|quantities?)\b",                                                                  RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.ExportData,
                    BaseConfidence = 0.82,
                    Patterns = new[]
                    {
                        new Regex(@"\b(exportar?|export)\b.{0,30}\b(datos?|data|csv|xml|landxml|excel|reporte?|report)\b", RxOpts),
                        new Regex(@"\b(generar?\s+reporte?|generate\s+report)\b",                                          RxOpts)
                    }
                },
                new()
                {
                    Intent  = CivilIntent.ApplyStandard,
                    BaseConfidence = 0.83,
                    Patterns = new[]
                    {
                        new Regex(@"\b(aplicar?|apply|usar?|use|seg[uú]n|according\s+to|per)\b.{0,20}\b(norma|est[aá]ndar|standard|interagua|inen|ieos)\b", RxOpts),
                        new Regex(@"\b(norma|est[aá]ndar|standard)\b.{0,20}\b(ecuatoriana?|ecuatoriano|inen|interagua)\b",                                  RxOpts)
                    }
                }
            };

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Parses <paramref name="command"/> and returns a structured
        /// <see cref="ExecutionPlan"/> containing all workflow steps and API calls.
        /// </summary>
        /// <param name="command">Natural language command in Spanish or English.</param>
        /// <returns>An <see cref="ExecutionPlan"/> (check <see cref="ExecutionPlan.IsValid"/>).</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is null.</exception>
        public ExecutionPlan Parse(string command)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));

            string normalised = NormaliseInput(command);
            string language   = DetectLanguage(normalised);

            var (intent, confidence) = MatchIntent(normalised);
            var parameters           = ExtractParameters(normalised);
            string? standard         = ExtractStandardReference(normalised);

            var request = new WorkflowRequest
            {
                Intent              = intent,
                OriginalCommand     = command,
                Language            = language,
                ExtractedParameters = parameters,
                ConfidenceScore     = confidence,
                StandardReference   = standard
            };

            var steps   = BuildWorkflowSteps(request);
            string summary = BuildSummary(request, steps.Count);
            string? warning = BuildWarning(request);

            return new ExecutionPlan
            {
                Request           = request,
                Steps             = steps,
                Summary           = summary,
                Warning           = warning,
                EstimatedDuration = EstimateDuration(intent, parameters)
            };
        }

        /// <summary>
        /// Batch-parses a list of commands and returns their execution plans.
        /// </summary>
        public IReadOnlyList<ExecutionPlan> ParseBatch(IEnumerable<string> commands)
        {
            if (commands is null) throw new ArgumentNullException(nameof(commands));
            return commands.Select(Parse).ToList().AsReadOnly();
        }

        // ── Normalisation ──────────────────────────────────────────────────────

        private static string NormaliseInput(string input)
        {
            // Collapse whitespace, strip leading/trailing spaces, lower-case.
            return Regex.Replace(input.Trim(), @"\s+", " ").ToLowerInvariant();
        }

        private static string DetectLanguage(string text)
        {
            // Cheap heuristic: look for Spanish-specific markers.
            if (Regex.IsMatch(text, @"\b(crear|generar|corredor|superficie|tuber[ií]a|pendiente|seg[uú]n|validar|calcular)\b", RxOpts))
                return "es";
            return "en";
        }

        // ── Intent matching ────────────────────────────────────────────────────

        private static (CivilIntent intent, double confidence) MatchIntent(string text)
        {
            double bestScore  = 0.0;
            var    bestIntent = CivilIntent.Unknown;

            foreach (var pattern in IntentPatterns)
            {
                int matchCount = pattern.Patterns.Count(p => p.IsMatch(text));
                if (matchCount == 0) continue;

                // Confidence rises with the fraction of patterns that match.
                double score = pattern.BaseConfidence
                             * (0.7 + 0.3 * matchCount / pattern.Patterns.Length);

                if (score > bestScore)
                {
                    bestScore  = score;
                    bestIntent = pattern.Intent;
                }
            }

            return (bestIntent, bestScore);
        }

        // ── Parameter extraction ───────────────────────────────────────────────

        private static IReadOnlyDictionary<string, object> ExtractParameters(string text)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // Length / distance
            var lenMatch = RxLength.Match(text);
            if (lenMatch.Success && double.TryParse(
                    lenMatch.Groups["value"].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double lenVal))
            {
                string unit = lenMatch.Groups["unit"].Value.ToLowerInvariant();
                double metres = unit is "km" or "kilometros" or "kilometer" or "kilometers"
                    ? lenVal * 1000.0
                    : unit is "ft" or "feet"
                    ? lenVal * 0.3048
                    : lenVal;
                result["length_m"] = metres;
            }

            // Diameter
            var diaMatch = RxDiameter.Match(text);
            if (diaMatch.Success && double.TryParse(
                    diaMatch.Groups["value"].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double diaVal))
            {
                string unit = diaMatch.Groups["unit"].Value.ToLowerInvariant();
                double mm = unit is "cm" ? diaVal * 10.0
                          : unit is "m"  ? diaVal * 1000.0
                          : unit is "in" or "pulg" or "pulgada" ? diaVal * 25.4
                          : diaVal;
                result["diameter_mm"] = mm;
            }

            // Slope / grade
            var slopeMatch = RxSlope.Match(text);
            if (slopeMatch.Success && double.TryParse(
                    slopeMatch.Groups["value"].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double slopeVal))
            {
                result["slope_pct"] = slopeVal;
            }

            // Named object (quoted or "llamado/named" prefix)
            var nameMatch = RxName.Match(text);
            if (nameMatch.Success)
            {
                string name = nameMatch.Groups[1].Value
                           ?? nameMatch.Groups[2].Value
                           ?? nameMatch.Groups[3].Value;
                if (!string.IsNullOrEmpty(name))
                    result["object_name"] = name;
            }

            // Design speed
            var speedMatch = Regex.Match(text,
                @"(?<v>\d+)\s*(?:km[/\s]?h|kph|mph|velocidad)", RxOpts);
            if (speedMatch.Success && int.TryParse(speedMatch.Groups["v"].Value, out int speed))
                result["design_speed_kmh"] = speed;

            // Alignment name pattern (EJE-XX, AL-XX)
            var axisMatch = Regex.Match(text,
                @"\b(eje[- _]?\d+|al[- _]?\d+|alignment[- _]?\w+)\b", RxOpts);
            if (axisMatch.Success)
                result["alignment_name"] = axisMatch.Value.ToUpperInvariant();

            return result;
        }

        // ── Standard reference extraction ──────────────────────────────────────

        private static string? ExtractStandardReference(string text)
        {
            foreach (var (keyword, standardId) in StandardKeywords)
            {
                if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return standardId;
            }
            return null;
        }

        // ── Workflow step builder ──────────────────────────────────────────────

        private static IReadOnlyList<WorkflowStep> BuildWorkflowSteps(WorkflowRequest request)
        {
            return request.Intent switch
            {
                CivilIntent.CreateCorridor        => BuildCorridorSteps(request),
                CivilIntent.CreateAlignment       => BuildAlignmentSteps(request),
                CivilIntent.CreateProfile         => BuildProfileSteps(request),
                CivilIntent.CreateSanitaryNetwork => BuildPipeNetworkSteps(request, "Sanitary"),
                CivilIntent.CreateStormNetwork    => BuildPipeNetworkSteps(request, "Storm"),
                CivilIntent.CreatePipeNetwork     => BuildPipeNetworkSteps(request, "Water"),
                CivilIntent.AnalyzeSurface        => BuildSurfaceAnalysisSteps(request),
                CivilIntent.CreateSurface         => BuildSurfaceCreationSteps(request),
                CivilIntent.ValidateSlopes        => BuildSlopeValidationSteps(request),
                CivilIntent.ValidatePipeNetwork   => BuildPipeValidationSteps(request),
                CivilIntent.CalculateVolumes      => BuildVolumeCalcSteps(request),
                CivilIntent.GenerateQuantities    => BuildQuantitiesSteps(request),
                CivilIntent.ExportData            => BuildExportSteps(request),
                CivilIntent.ApplyStandard         => BuildApplyStandardSteps(request),
                _                                 => Array.Empty<WorkflowStep>()
            };
        }

        // ── Step builders ──────────────────────────────────────────────────────

        private static IReadOnlyList<WorkflowStep> BuildCorridorSteps(WorkflowRequest req)
        {
            req.ExtractedParameters.TryGetValue("alignment_name", out object? alignName);
            req.ExtractedParameters.TryGetValue("object_name",    out object? corridorName);
            req.ExtractedParameters.TryGetValue("length_m",       out object? lengthObj);

            string aName   = alignName?.ToString()   ?? "Alignment-1";
            string cName   = corridorName?.ToString() ?? "Corridor-1";
            double? length = lengthObj as double?;

            return new List<WorkflowStep>
            {
                Step(1, "Retrieve alignment from drawing",
                    Call(Civil3DApiOperation.AlignmentGetByName,
                        Param("name", aName), Param("documentScope", "ActiveDocument")),
                    rollback: null),

                Step(2, "Retrieve or create design profile on alignment",
                    Call(Civil3DApiOperation.ProfileGetByName,
                        Param("alignmentName", aName), Param("profileName", "Design Profile")),
                    rollback: "Delete profile if created"),

                Step(3, "Retrieve assembly for cross-section",
                    Call(Civil3DApiOperation.AssemblyGetByName,
                        Param("name", "Standard Assembly")),
                    rollback: null),

                Step(4, $"Create corridor '{cName}'",
                    Call(Civil3DApiOperation.CorridorCreate,
                        Param("name", cName),
                        Param("alignmentId", "$alignment"),
                        Param("profileId",   "$profile"),
                        Param("assemblyId",  "$assembly"),
                        length.HasValue ? Param("targetLength_m", length.Value) : Param("targetLength_m", "full")),
                    rollback: $"Delete corridor '{cName}'"),

                Step(5, "Add baseline to corridor",
                    Call(Civil3DApiOperation.CorridorAddBaseline,
                        Param("corridorId",   "$corridor"),
                        Param("alignmentId",  "$alignment"),
                        Param("profileId",    "$profile")),
                    rollback: "Remove added baseline"),

                Step(6, "Add region with assembly",
                    Call(Civil3DApiOperation.CorridorAddRegion,
                        Param("baselineId",  "$baseline"),
                        Param("assemblyId",  "$assembly"),
                        Param("startStation", 0.0),
                        Param("endStation",   length?.ToString() ?? "EndOfAlignment")),
                    rollback: "Remove region"),

                Step(7, "Rebuild corridor",
                    Call(Civil3DApiOperation.CorridorRebuild,
                        Param("corridorId", "$corridor")),
                    rollback: null)
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildAlignmentSteps(WorkflowRequest req)
        {
            req.ExtractedParameters.TryGetValue("object_name",       out object? nameObj);
            req.ExtractedParameters.TryGetValue("design_speed_kmh",  out object? speedObj);

            string aName = nameObj?.ToString() ?? "Alignment-1";
            object speed = speedObj ?? 60;

            return new List<WorkflowStep>
            {
                Step(1, $"Create alignment '{aName}'",
                    Call(Civil3DApiOperation.AlignmentCreate,
                        Param("name",            aName),
                        Param("siteId",          "ActiveSite"),
                        Param("alignmentStyle",  "Standard"),
                        Param("labelSetStyle",   "Standard")),
                    rollback: $"Delete alignment '{aName}'"),

                Step(2, "Set alignment design speed",
                    Call(Civil3DApiOperation.AlignmentSetDesignSpeed,
                        Param("alignmentId",     "$alignment"),
                        Param("designSpeed_kmh", speed)),
                    rollback: "Revert design speed")
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildProfileSteps(WorkflowRequest req)
        {
            req.ExtractedParameters.TryGetValue("alignment_name", out object? alignName);
            req.ExtractedParameters.TryGetValue("object_name",    out object? profileName);

            string aName = alignName?.ToString()   ?? "Alignment-1";
            string pName = profileName?.ToString() ?? "Design Profile";

            return new List<WorkflowStep>
            {
                Step(1, $"Get alignment '{aName}' for profile",
                    Call(Civil3DApiOperation.AlignmentGetByName, Param("name", aName)),
                    rollback: null),

                Step(2, $"Create profile '{pName}'",
                    Call(Civil3DApiOperation.ProfileCreate,
                        Param("name",         pName),
                        Param("alignmentId",  "$alignment"),
                        Param("profileStyle", "Design Profile")),
                    rollback: $"Delete profile '{pName}'"),

                Step(3, "Set grade breaks on profile",
                    Call(Civil3DApiOperation.ProfileSetGradeBreaks,
                        Param("profileId", "$profile")),
                    rollback: "Remove grade breaks",
                    optional: true)
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildPipeNetworkSteps(
            WorkflowRequest req, string networkType)
        {
            req.ExtractedParameters.TryGetValue("object_name",  out object? nameObj);
            req.ExtractedParameters.TryGetValue("diameter_mm",  out object? diaObj);

            string netName  = nameObj?.ToString() ?? $"{networkType} Network 1";
            double dia      = diaObj is double d ? d : 200.0;
            string partList = networkType == "Sanitary" ? "Sanitary Parts List"
                            : networkType == "Storm"    ? "Storm Parts List"
                            : "Water Parts List";

            return new List<WorkflowStep>
            {
                Step(1, $"Create {networkType.ToLower()} pipe network '{netName}'",
                    Call(Civil3DApiOperation.PipeNetworkCreate,
                        Param("name",          netName),
                        Param("networkType",   networkType),
                        Param("partsList",     partList),
                        Param("referenceAlignmentName", ""),
                        Param("referenceProfileName",   "")),
                    rollback: $"Delete network '{netName}'"),

                Step(2, "Lay out pipes along route",
                    Call(Civil3DApiOperation.PipeCreate,
                        Param("networkId",       "$network"),
                        Param("pipePartName",    $"Concrete Pipe {dia} mm"),
                        Param("diameter_mm",     dia),
                        Param("startPoint",      "LayoutStart"),
                        Param("endPoint",        "LayoutEnd")),
                    rollback: "Delete created pipes"),

                Step(3, "Place structures at pipe junctions",
                    Call(Civil3DApiOperation.StructureCreate,
                        Param("networkId",        "$network"),
                        Param("structurePartName", networkType == "Sanitary"
                                                    ? "Concrete Manhole"
                                                    : "Catch Basin"),
                        Param("insertionPoint",    "auto")),
                    rollback: "Delete created structures"),

                Step(4, "Apply label styles to network",
                    Call(Civil3DApiOperation.LabelStyleApply,
                        Param("networkId",     "$network"),
                        Param("pipeLabelStyle", $"{networkType} Pipe Label"),
                        Param("structureLabelStyle", $"{networkType} Structure Label")),
                    rollback: null, optional: true)
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildSurfaceAnalysisSteps(WorkflowRequest req)
        {
            req.ExtractedParameters.TryGetValue("object_name", out object? nameObj);
            string sName = nameObj?.ToString() ?? "Existing Ground";

            return new List<WorkflowStep>
            {
                Step(1, $"Get surface '{sName}'",
                    Call(Civil3DApiOperation.SurfaceGetByName, Param("name", sName)),
                    rollback: null),

                Step(2, "Extract contour lines from surface",
                    Call(Civil3DApiOperation.SurfaceExtractContours,
                        Param("surfaceId",      "$surface"),
                        Param("minorInterval",  1.0),
                        Param("majorInterval",  5.0)),
                    rollback: "Delete extracted contours"),

                Step(3, "Sample elevation grid across surface",
                    Call(Civil3DApiOperation.SurfaceSampleElevation,
                        Param("surfaceId", "$surface"),
                        Param("gridSpacing_m", 5.0)),
                    rollback: null)
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildSurfaceCreationSteps(WorkflowRequest req)
        {
            req.ExtractedParameters.TryGetValue("object_name", out object? nameObj);
            string sName = nameObj?.ToString() ?? "New Surface";

            return new List<WorkflowStep>
            {
                Step(1, $"Create TIN surface '{sName}'",
                    Call(Civil3DApiOperation.SurfaceCreate,
                        Param("name",         sName),
                        Param("surfaceStyle", "Standard"),
                        Param("surfaceType",  "TinSurface")),
                    rollback: $"Delete surface '{sName}'"),

                Step(2, "Add contour data to surface",
                    Call(Civil3DApiOperation.SurfaceAddContourData,
                        Param("surfaceId",      "$surface"),
                        Param("contourSource",  "SelectFromDrawing")),
                    rollback: "Remove contour data from surface")
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildSlopeValidationSteps(WorkflowRequest req)
        {
            double maxSlope = 12.0;  // default max %
            double minSlope = 0.5;   // default min %
            if (req.ExtractedParameters.TryGetValue("slope_pct", out object? slopeObj)
                && slopeObj is double s)
                maxSlope = s;

            string? standard = req.StandardReference;

            return new List<WorkflowStep>
            {
                Step(1, "Retrieve corridor or alignment for slope check",
                    Call(Civil3DApiOperation.AlignmentGetByName,
                        Param("name", req.ExtractedParameters.TryGetValue("alignment_name", out object? an)
                                      ? an?.ToString() ?? "Alignment-1" : "Alignment-1")),
                    rollback: null),

                Step(2, $"Validate slopes (min {minSlope}%, max {maxSlope}%)" +
                        (standard is not null ? $" per {standard}" : ""),
                    Call(Civil3DApiOperation.SlopeValidate,
                        Param("profileId",   "$profile"),
                        Param("minSlope_pct", minSlope),
                        Param("maxSlope_pct", maxSlope),
                        Param("standard",     standard ?? "MTOP-2013")),
                    rollback: null)
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildPipeValidationSteps(WorkflowRequest req)
        {
            string? standard = req.StandardReference ?? "INTERAGUA-SDS-2015";

            return new List<WorkflowStep>
            {
                Step(1, "Retrieve pipe network for validation",
                    Call(Civil3DApiOperation.PipeNetworkGetByName,
                        Param("name", req.ExtractedParameters.TryGetValue("object_name", out object? nn)
                                      ? nn?.ToString() ?? "Network-1" : "Network-1")),
                    rollback: null),

                Step(2, $"Validate pipe diameters per {standard}",
                    Call(Civil3DApiOperation.DiameterValidate,
                        Param("networkId",  "$network"),
                        Param("minDia_mm",  200.0),
                        Param("standard",   standard)),
                    rollback: null),

                Step(3, $"Validate cover depths per {standard}",
                    Call(Civil3DApiOperation.CoverDepthValidate,
                        Param("networkId",      "$network"),
                        Param("minCover_m",     1.2),
                        Param("maxCover_m",     5.0),
                        Param("referenceGradeFromSurface", true),
                        Param("standard",       standard)),
                    rollback: null),

                Step(4, "Validate pipe slopes",
                    Call(Civil3DApiOperation.SlopeValidate,
                        Param("networkId",    "$network"),
                        Param("minSlope_pct", 0.5),
                        Param("maxSlope_pct", 10.0),
                        Param("standard",     standard)),
                    rollback: null)
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildVolumeCalcSteps(WorkflowRequest req)
        {
            return new List<WorkflowStep>
            {
                Step(1, "Get existing ground surface",
                    Call(Civil3DApiOperation.SurfaceGetByName,
                        Param("name", "Existing Ground")),
                    rollback: null),

                Step(2, "Get finished grade surface",
                    Call(Civil3DApiOperation.SurfaceGetByName,
                        Param("name", "Finished Grade")),
                    rollback: null),

                Step(3, "Create TIN volume surface between existing and finished",
                    Call(Civil3DApiOperation.TinVolumeSurfaceCreate,
                        Param("name",            "Volume Surface"),
                        Param("baseSurfaceId",   "$existingSurface"),
                        Param("compSurfaceId",   "$finishedSurface")),
                    rollback: "Delete volume surface")
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildQuantitiesSteps(WorkflowRequest req)
        {
            return new List<WorkflowStep>
            {
                Step(1, "Get corridor for quantity takeoff",
                    Call(Civil3DApiOperation.AlignmentGetByName,
                        Param("name", req.ExtractedParameters.TryGetValue("alignment_name", out object? an)
                                      ? an?.ToString() ?? "Alignment-1" : "Alignment-1")),
                    rollback: null),

                Step(2, "Calculate material volumes from corridor",
                    Call(Civil3DApiOperation.CorridorRebuild,
                        Param("corridorId",        "$corridor"),
                        Param("generateQuantities", true)),
                    rollback: null),

                Step(3, "Export quantities to report",
                    Call(Civil3DApiOperation.ExportCsv,
                        Param("outputPath",   "quantities_report.csv"),
                        Param("includeUnits", true)),
                    rollback: "Delete generated CSV", optional: true)
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildExportSteps(WorkflowRequest req)
        {
            bool landXml = req.OriginalCommand.Contains("landxml", StringComparison.OrdinalIgnoreCase)
                        || req.OriginalCommand.Contains("xml", StringComparison.OrdinalIgnoreCase);

            return new List<WorkflowStep>
            {
                Step(1, landXml ? "Export to LandXML" : "Export to CSV",
                    landXml
                        ? Call(Civil3DApiOperation.ExportLandXml,
                              Param("outputPath",  "export.xml"),
                              Param("version",     "1.2"),
                              Param("includeAll",  true))
                        : Call(Civil3DApiOperation.ExportCsv,
                              Param("outputPath",  "export.csv"),
                              Param("delimiter",   ",")),
                    rollback: "Delete export file")
            }.AsReadOnly();
        }

        private static IReadOnlyList<WorkflowStep> BuildApplyStandardSteps(WorkflowRequest req)
        {
            string standard = req.StandardReference ?? "INTERAGUA-SDS-2015";

            return new List<WorkflowStep>
            {
                Step(1, $"Load standard configuration: {standard}",
                    Call(Civil3DApiOperation.LabelStyleApply,
                        Param("standardId",  standard),
                        Param("scope",       "ActiveDocument")),
                    rollback: "Revert to previous standard")
            }.AsReadOnly();
        }

        // ── Helper factory methods ─────────────────────────────────────────────

        private static WorkflowStep Step(
            int    number,
            string description,
            ApiCall call,
            string? rollback  = null,
            bool   optional   = false)
        {
            return new WorkflowStep
            {
                StepNumber          = number,
                Description         = description,
                ApiCalls            = new[] { call },
                RollbackDescription = rollback,
                IsOptional          = optional
            };
        }

        private static ApiCall Call(Civil3DApiOperation op, params ApiParameter[] ps)
        {
            return new ApiCall
            {
                Operation  = op,
                Parameters = ps,
                ReturnVariable = "$" + op.ToString().Replace("Create", "")
                                              .Replace("Get", "")
                                              .Replace("ByName", "")
                                              .ToLower()
            };
        }

        private static ApiParameter Param(string name, object value, string unit = "")
        {
            return new ApiParameter { Name = name, Value = value, Unit = unit };
        }

        // ── Summary / warning builders ─────────────────────────────────────────

        private static string BuildSummary(WorkflowRequest req, int stepCount)
        {
            string intentLabel = req.Language == "es"
                ? IntentToSpanish(req.Intent)
                : req.Intent.ToString();

            string paramsDesc = req.ExtractedParameters.Count > 0
                ? $" [{string.Join(", ", req.ExtractedParameters.Select(kv => $"{kv.Key}={kv.Value}"))}]"
                : "";

            string stdDesc = req.StandardReference is not null
                ? $" (Standard: {req.StandardReference})"
                : "";

            return $"{intentLabel}{paramsDesc} → {stepCount} step(s) planned{stdDesc}. "
                 + $"Confidence: {req.ConfidenceScore:P0}";
        }

        private static string? BuildWarning(WorkflowRequest req)
        {
            if (req.ConfidenceScore < 0.5)
                return "Low confidence: command may be ambiguous. Review the extracted intent.";
            if (req.Intent == CivilIntent.Unknown)
                return "Intent could not be determined from the command.";
            return null;
        }

        private static string IntentToSpanish(CivilIntent intent) => intent switch
        {
            CivilIntent.CreateCorridor        => "Crear corredor",
            CivilIntent.CreateAlignment       => "Crear alineación",
            CivilIntent.CreateProfile         => "Crear perfil",
            CivilIntent.CreatePipeNetwork     => "Crear red de tuberías",
            CivilIntent.CreateSanitaryNetwork => "Crear red sanitaria",
            CivilIntent.CreateStormNetwork    => "Crear red pluvial",
            CivilIntent.AnalyzeSurface        => "Analizar superficie",
            CivilIntent.CreateSurface         => "Crear superficie",
            CivilIntent.ValidateSlopes        => "Validar pendientes",
            CivilIntent.ValidatePipeNetwork   => "Validar red de tuberías",
            CivilIntent.CalculateVolumes      => "Calcular volúmenes",
            CivilIntent.GenerateQuantities    => "Generar metrados",
            CivilIntent.ExportData            => "Exportar datos",
            CivilIntent.ApplyStandard         => "Aplicar norma",
            _                                 => "Operación desconocida"
        };

        private static TimeSpan EstimateDuration(
            CivilIntent intent,
            IReadOnlyDictionary<string, object> parameters)
        {
            double baseSecs = intent switch
            {
                CivilIntent.CreateCorridor        => 30,
                CivilIntent.CreatePipeNetwork     => 20,
                CivilIntent.CreateSanitaryNetwork => 25,
                CivilIntent.CreateStormNetwork    => 25,
                CivilIntent.AnalyzeSurface        => 15,
                CivilIntent.CreateSurface         => 20,
                CivilIntent.CalculateVolumes      => 10,
                CivilIntent.ValidateSlopes        => 8,
                CivilIntent.ValidatePipeNetwork   => 12,
                CivilIntent.GenerateQuantities    => 15,
                CivilIntent.ExportData            => 5,
                _                                 => 5
            };

            // Scale estimate up for longer corridors/networks.
            if (parameters.TryGetValue("length_m", out object? lenObj) && lenObj is double len)
                baseSecs *= Math.Max(1.0, len / 1000.0);

            return TimeSpan.FromSeconds(baseSecs);
        }
    }
}
