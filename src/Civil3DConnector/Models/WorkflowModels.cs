using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace Civil3DConnector.Models
{
    // ─────────────────────────────────────────────────────────────────────────
    // AI / Workflow Models
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Represents the parsed intent from a natural language command.</summary>
    public class ExecutionPlan
    {
        public string OriginalCommand { get; set; } = "";
        public string IntentDescription { get; set; } = "";
        public IntentType Intent { get; set; }
        public double Confidence { get; set; }
        public List<WorkflowStep> Steps { get; set; } = new();
        public List<string> RequiredApis { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string GeneratedCode { get; set; } = "";
        public List<string> Warnings { get; set; } = new();
    }

    public class WorkflowStep
    {
        public int Order { get; set; }
        public string Description { get; set; } = "";
        public string ApiMethod { get; set; } = "";
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string RollbackProcedure { get; set; } = "";
        public bool IsOptional { get; set; }
    }

    public enum IntentType
    {
        Unknown,
        CreateCorridor,
        CreatePipeNetwork,
        CreateAlignment,
        CreateProfile,
        CreateSurface,
        AnalyzeDwg,
        ValidateStandards,
        ExportGis,
        GenerateDynamo,
        GeneratePlugin,
        CalculateVolume,
        DetectInterferences,
        BatchLabel,
        ExportLandXml,
        ImportGeoJson
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Standards Violation Models
    // ─────────────────────────────────────────────────────────────────────────

    public class StandardsViolation
    {
        public string ObjectName { get; set; } = "";
        public string Standard { get; set; } = "";
        public string ViolationMessage { get; set; } = "";
        public double ActualValue { get; set; }
        public double LimitValue { get; set; }
        public string Unit { get; set; } = "";
        public ViolationSeverity Severity { get; set; } = ViolationSeverity.Warning;
        public string FixSuggestion { get; set; } = "";
        public ObjectId ObjectId { get; set; }
    }

    public enum ViolationSeverity
    {
        Info,
        Warning,
        Critical
    }

    public enum ValidatorStandard
    {
        All,
        Interagua,
        Amagua,
        MTOP,
        NEC,
        NteInen,
        MunicipalQuito,
        MunicipalGuayaquil
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Analysis Models
    // ─────────────────────────────────────────────────────────────────────────

    public class ObjectIssue
    {
        public string ObjectName { get; set; } = "";
        public string Category { get; set; } = "";
        public string Description { get; set; } = "";
        public IssueSeverity Severity { get; set; }
        public string FixSuggestion { get; set; } = "";
        public ObjectId ObjectId { get; set; }
        public string ObjectType { get; set; } = "";
    }

    public enum IssueSeverity
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    public class AnalysisResult
    {
        public DateTime AnalysisDate { get; set; } = DateTime.Now;
        public string DrawingPath { get; set; } = "";
        public string CivilVersion { get; set; } = "";
        public int TotalObjectsScanned { get; set; }
        public List<ObjectIssue> CriticalIssues { get; set; } = new();
        public List<ObjectIssue> Warnings { get; set; } = new();
        public List<ObjectIssue> InfoItems { get; set; } = new();
        public Dictionary<string, int> ObjectCountByType { get; set; } = new();
        public TimeSpan AnalysisDuration { get; set; }
        public bool HasCorruptedObjects => CriticalIssues.Exists(i => i.Category == "Corruption");
        public bool HasMissingReferences => CriticalIssues.Exists(i => i.Category == "Reference");
        public int TotalIssues => CriticalIssues.Count + Warnings.Count;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Plugin Generator Models
    // ─────────────────────────────────────────────────────────────────────────

    public class PluginGeneratorOptions
    {
        public string PluginName { get; set; } = "MyPlugin";
        public string Description { get; set; } = "";
        public string[] TargetVersions { get; set; } = { "2025", "2026", "2027" };
        public bool IncludeRibbon { get; set; } = true;
        public bool IncludePalette { get; set; } = true;
        public bool IncludeEventHandlers { get; set; } = true;
        public string OutputDirectory { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string CompanyName { get; set; } = "";
        public List<string> CustomCommands { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Interference Detection Models
    // ─────────────────────────────────────────────────────────────────────────

    public class InterferenceResult
    {
        public string Network1 { get; set; } = "";
        public string Network2 { get; set; } = "";
        public string Pipe1Name { get; set; } = "";
        public string Pipe2Name { get; set; } = "";
        public Point3dCollection IntersectionPoints { get; set; } = new();
        public double MinimumDistance { get; set; }
        public double RequiredClearance { get; set; }
        public bool IsViolation => MinimumDistance < RequiredClearance;
    }
}
