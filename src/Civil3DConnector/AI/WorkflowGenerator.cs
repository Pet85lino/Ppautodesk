using System;
using System.Collections.Generic;
using System.Linq;

namespace Autodesk.Civil3D.Connector.AI
{
    // ─────────────────────────────────────────────────────────────────────────
    // Shared enums / value types for workflows
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The high-level category of a workflow.</summary>
    public enum WorkflowCategory
    {
        Corridor,
        PipeNetwork,
        SiteGrading,
        RoadDesign
    }

    /// <summary>Possible result statuses when a step executes.</summary>
    public enum StepStatus
    {
        Pending,
        Running,
        Completed,
        Failed,
        Skipped,
        RolledBack
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Workflow / step model
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Holds a name-typed Civil 3D API call with ordered, named parameters.</summary>
    public sealed class WorkflowApiCall
    {
        /// <summary>e.g. "CivilDocument.GetCorridorByName"</summary>
        public string ApiMethod         { get; init; } = string.Empty;

        /// <summary>Fully-qualified namespace of the method, e.g. "Autodesk.Civil.DatabaseServices".</summary>
        public string Namespace         { get; init; } = string.Empty;

        /// <summary>Ordered list of parameter name → value pairs.</summary>
        public IReadOnlyList<(string Name, object Value)> Parameters { get; init; }
            = Array.Empty<(string, object)>();

        /// <summary>Variable name that receives the return value.</summary>
        public string? OutVariable      { get; init; }

        /// <summary>Whether this call requires an active AutoCAD transaction.</summary>
        public bool RequiresTransaction { get; init; } = true;

        public override string ToString()
        {
            string args = string.Join(", ",
                Parameters.Select(p => $"{p.Name}: {p.Value}"));
            string ret  = OutVariable is null ? "" : $" → {OutVariable}";
            return $"{Namespace}.{ApiMethod}({args}){ret}";
        }
    }

    /// <summary>
    /// One ordered, atomic step within a <see cref="GeneratedWorkflow"/>.
    /// </summary>
    public sealed class WorkflowStep
    {
        public int     StepNumber       { get; init; }
        public string  StepId           { get; init; } = Guid.NewGuid().ToString("N")[..8];
        public string  Name             { get; init; } = string.Empty;
        public string  Description      { get; init; } = string.Empty;
        public string  Category         { get; init; } = string.Empty;

        /// <summary>Ordered API calls that implement this step.</summary>
        public IReadOnlyList<WorkflowApiCall> ApiCalls { get; init; } = Array.Empty<WorkflowApiCall>();

        /// <summary>Human-readable procedure to undo this step.</summary>
        public string? RollbackProcedure { get; init; }

        /// <summary>AutoCAD .NET: whether the step must be wrapped in a Transaction.</summary>
        public bool    RequiresTransaction { get; init; } = true;

        /// <summary>Whether the overall workflow can continue if this step fails.</summary>
        public bool    IsCritical         { get; init; } = true;

        /// <summary>Estimated execution time.</summary>
        public TimeSpan EstimatedDuration  { get; init; }

        public StepStatus Status { get; set; } = StepStatus.Pending;

        public override string ToString() =>
            $"[{StepNumber:D2}] {Name} ({ApiCalls.Count} API call(s))";
    }

    /// <summary>Immutable generated workflow with all steps and metadata.</summary>
    public sealed class GeneratedWorkflow
    {
        public string           WorkflowId      { get; init; } = Guid.NewGuid().ToString("N")[..12];
        public WorkflowCategory Category        { get; init; }
        public string           Name            { get; init; } = string.Empty;
        public string           Description     { get; init; } = string.Empty;
        public string           Version         { get; init; } = "1.0";
        public DateTime         CreatedAt       { get; init; } = DateTime.UtcNow;

        public IReadOnlyList<WorkflowStep> Steps { get; init; } = Array.Empty<WorkflowStep>();

        /// <summary>Required Civil 3D version range.</summary>
        public (int Min, int Max) SupportedVersions { get; init; } = (2025, 2027);

        /// <summary>DLL assemblies that must be referenced before execution.</summary>
        public IReadOnlyList<string> RequiredAssemblies { get; init; } = Array.Empty<string>();

        public TimeSpan TotalEstimatedDuration =>
            TimeSpan.FromSeconds(Steps.Sum(s => s.EstimatedDuration.TotalSeconds));

        public int CriticalStepCount =>
            Steps.Count(s => s.IsCritical);

        public override string ToString() =>
            $"{Name} [{Category}] — {Steps.Count} step(s), ~{TotalEstimatedDuration.TotalSeconds:F0}s";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WorkflowGeneratorOptions
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Options used to customise the generated workflow.
    /// All properties have sensible defaults.
    /// </summary>
    public sealed class WorkflowGeneratorOptions
    {
        // Shared
        public string AlignmentName      { get; init; } = "Alignment-1";
        public string ProfileName        { get; init; } = "Design Profile";
        public string AssemblyName       { get; init; } = "Standard Assembly";
        public string ExistingSurface    { get; init; } = "Existing Ground";
        public string FinishedSurface    { get; init; } = "Finished Grade";
        public string CorridorName       { get; init; } = "Corridor-1";
        public double StartStation       { get; init; } = 0.0;
        public double EndStation         { get; init; } = -1.0; // -1 = use full alignment length
        public double TargetLength_m     { get; init; } = 500.0;
        public string StandardReference  { get; init; } = "MTOP-2013";

        // Pipe network
        public string NetworkName        { get; init; } = "Pipe Network 1";
        public string NetworkType        { get; init; } = "Sanitary";  // Sanitary | Storm | Water
        public double PipeDiameter_mm    { get; init; } = 200.0;
        public double MinCoverDepth_m    { get; init; } = 1.20;
        public double MaxCoverDepth_m    { get; init; } = 5.00;
        public double MinSlope_pct       { get; init; } = 0.50;
        public double MaxSlope_pct       { get; init; } = 10.00;

        // Road design
        public int    DesignSpeed_kmh    { get; init; } = 60;
        public double RightOfWay_m       { get; init; } = 20.0;
        public double LaneWidth_m        { get; init; } = 3.65;
        public int    NumberOfLanes      { get; init; } = 2;

        // Site grading
        public double GradingOffset_m    { get; init; } = 5.0;
        public double CutSlope           { get; init; } = 1.5;  // H:V
        public double FillSlope          { get; init; } = 2.0;  // H:V

        // Output
        public bool   GenerateReport     { get; init; } = true;
        public string ReportOutputPath   { get; init; } = "workflow_report.csv";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WorkflowGenerator
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates complete, ordered Civil 3D workflow objects for the four
    /// primary design domains: corridor, pipe network, site grading, road design.
    /// </summary>
    public sealed class WorkflowGenerator
    {
        // ── Public factory methods ─────────────────────────────────────────────

        /// <summary>
        /// Generates a corridor creation workflow:
        /// Alignment → Profile → Assembly → Corridor → Rebuild.
        /// </summary>
        public GeneratedWorkflow CreateCorridorWorkflow(WorkflowGeneratorOptions? opts = null)
        {
            opts ??= new WorkflowGeneratorOptions();

            double endStation = opts.EndStation < 0
                ? opts.StartStation + opts.TargetLength_m
                : opts.EndStation;

            var steps = new List<WorkflowStep>
            {
                CorridorStep_GetAlignment(opts),
                CorridorStep_GetProfile(opts),
                CorridorStep_GetAssembly(opts),
                CorridorStep_Create(opts, endStation),
                CorridorStep_AddBaseline(opts),
                CorridorStep_AddRegion(opts, endStation),
                CorridorStep_SetTargets(opts),
                CorridorStep_Rebuild(opts),
                CorridorStep_ApplyLabelStyles(opts),
                CorridorStep_ExportReport(opts)
            };

            RenumberSteps(steps);

            return new GeneratedWorkflow
            {
                Category    = WorkflowCategory.Corridor,
                Name        = $"Create Corridor: {opts.CorridorName}",
                Description = $"Full corridor creation workflow for alignment '{opts.AlignmentName}', "
                            + $"profile '{opts.ProfileName}', assembly '{opts.AssemblyName}'. "
                            + $"Stations {opts.StartStation:F2}–{endStation:F2} m.",
                Steps       = steps.AsReadOnly(),
                RequiredAssemblies = RequiredDlls("roadway")
            };
        }

        /// <summary>
        /// Generates a pipe network workflow:
        /// Layout → Pipes → Structures → Cover check → Labels.
        /// </summary>
        public GeneratedWorkflow CreatePipeNetworkWorkflow(WorkflowGeneratorOptions? opts = null)
        {
            opts ??= new WorkflowGeneratorOptions();

            string partList = opts.NetworkType switch
            {
                "Storm"  => "Storm Drain Parts List",
                "Water"  => "Water Distribution Parts List",
                _        => "Sanitary Sewer Parts List"
            };

            var steps = new List<WorkflowStep>
            {
                PipeStep_CreateNetwork(opts, partList),
                PipeStep_SetLayoutRules(opts),
                PipeStep_LayoutPipes(opts),
                PipeStep_CreateStructures(opts),
                PipeStep_SetReferenceAlignment(opts),
                PipeStep_AssignPipeToSurface(opts),
                PipeStep_ApplyLabelStyles(opts),
                PipeStep_ValidateCoverDepths(opts),
                PipeStep_ValidateSlopes(opts),
                PipeStep_ExportReport(opts)
            };

            RenumberSteps(steps);

            return new GeneratedWorkflow
            {
                Category    = WorkflowCategory.PipeNetwork,
                Name        = $"Create {opts.NetworkType} Network: {opts.NetworkName}",
                Description = $"Pipe network workflow for a {opts.NetworkType.ToLower()} system. "
                            + $"Diameter: {opts.PipeDiameter_mm} mm. "
                            + $"Cover: {opts.MinCoverDepth_m}–{opts.MaxCoverDepth_m} m. "
                            + $"Standard: {opts.StandardReference}.",
                Steps       = steps.AsReadOnly(),
                RequiredAssemblies = RequiredDlls("pipe")
            };
        }

        /// <summary>
        /// Generates a site grading workflow:
        /// Existing surface → Grading object → Criteria → Volume surface → Report.
        /// </summary>
        public GeneratedWorkflow CreateSiteGradingWorkflow(WorkflowGeneratorOptions? opts = null)
        {
            opts ??= new WorkflowGeneratorOptions();

            var steps = new List<WorkflowStep>
            {
                GradeStep_GetExistingSurface(opts),
                GradeStep_CreateGradingGroup(opts),
                GradeStep_SetGradingCriteria(opts),
                GradeStep_CreateGradingObject(opts),
                GradeStep_CreateFinishedSurface(opts),
                GradeStep_CreateVolumeSurface(opts),
                GradeStep_ExtractContours(opts),
                GradeStep_CalculateVolumes(opts),
                GradeStep_ExportReport(opts)
            };

            RenumberSteps(steps);

            return new GeneratedWorkflow
            {
                Category    = WorkflowCategory.SiteGrading,
                Name        = $"Site Grading: {opts.ExistingSurface} → {opts.FinishedSurface}",
                Description = $"Site grading workflow from '{opts.ExistingSurface}' to "
                            + $"'{opts.FinishedSurface}'. "
                            + $"Cut slope {opts.CutSlope}:1, fill slope {opts.FillSlope}:1. "
                            + $"Offset: {opts.GradingOffset_m} m.",
                Steps       = steps.AsReadOnly(),
                RequiredAssemblies = RequiredDlls("grading")
            };
        }

        /// <summary>
        /// Generates a road design workflow:
        /// Alignment → Profile (existing + design) → Cross-sections → Quantities.
        /// </summary>
        public GeneratedWorkflow CreateRoadDesignWorkflow(WorkflowGeneratorOptions? opts = null)
        {
            opts ??= new WorkflowGeneratorOptions();

            var steps = new List<WorkflowStep>
            {
                RoadStep_CreateAlignment(opts),
                RoadStep_SampleExistingProfile(opts),
                RoadStep_CreateDesignProfile(opts),
                RoadStep_SetVerticalGeometry(opts),
                RoadStep_CreateAssembly(opts),
                RoadStep_AddSubassemblies(opts),
                RoadStep_CreateCorridor(opts),
                RoadStep_CreateSampleLines(opts),
                RoadStep_CreateCrossSections(opts),
                RoadStep_GenerateQuantities(opts),
                RoadStep_ExportQuantities(opts)
            };

            RenumberSteps(steps);

            return new GeneratedWorkflow
            {
                Category    = WorkflowCategory.RoadDesign,
                Name        = $"Road Design: {opts.AlignmentName}",
                Description = $"Complete road design workflow for alignment '{opts.AlignmentName}'. "
                            + $"Design speed: {opts.DesignSpeed_kmh} km/h. "
                            + $"ROW: {opts.RightOfWay_m} m. "
                            + $"Lanes: {opts.NumberOfLanes} × {opts.LaneWidth_m} m. "
                            + $"Standard: {opts.StandardReference}.",
                Steps       = steps.AsReadOnly(),
                RequiredAssemblies = RequiredDlls("roadway", "pipe")
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Corridor step builders
        // ─────────────────────────────────────────────────────────────────────

        private static WorkflowStep CorridorStep_GetAlignment(WorkflowGeneratorOptions o) =>
            MakeStep("Get Alignment",
                "Retrieve the horizontal alignment object from the Civil 3D document.",
                "Alignment",
                rollback: null,
                Api("CivilDocument.GetAlignmentByName",
                    "Autodesk.Civil.ApplicationServices",
                    ("name", o.AlignmentName),
                    ("outVar", "$alignment")));

        private static WorkflowStep CorridorStep_GetProfile(WorkflowGeneratorOptions o) =>
            MakeStep("Get Profile",
                $"Retrieve design profile '{o.ProfileName}' from alignment.",
                "Profile",
                rollback: null,
                Api("Alignment.GetProfileByName",
                    "Autodesk.Civil.DatabaseServices",
                    ("alignmentId", "$alignment"),
                    ("profileName", o.ProfileName),
                    ("outVar", "$profile")));

        private static WorkflowStep CorridorStep_GetAssembly(WorkflowGeneratorOptions o) =>
            MakeStep("Get Assembly",
                $"Retrieve cross-section assembly '{o.AssemblyName}'.",
                "Assembly",
                rollback: null,
                Api("CivilDocument.GetAssemblyByName",
                    "Autodesk.Civil.ApplicationServices",
                    ("name", o.AssemblyName),
                    ("outVar", "$assembly")));

        private static WorkflowStep CorridorStep_Create(
            WorkflowGeneratorOptions o, double endStation) =>
            MakeStep("Create Corridor",
                $"Create corridor '{o.CorridorName}' from alignment/profile/assembly.",
                "Corridor",
                rollback: $"Delete corridor '{o.CorridorName}' from the drawing.",
                Api("CorridorCollection.Add",
                    "Autodesk.Civil.DatabaseServices",
                    ("name",         o.CorridorName),
                    ("alignmentId",  "$alignment"),
                    ("profileId",    "$profile"),
                    ("assemblyId",   "$assembly"),
                    ("startStation", o.StartStation),
                    ("endStation",   endStation),
                    ("outVar",       "$corridor")));

        private static WorkflowStep CorridorStep_AddBaseline(WorkflowGeneratorOptions o) =>
            MakeStep("Add Baseline",
                "Add the alignment/profile pair as a corridor baseline.",
                "Corridor",
                rollback: "Remove the added baseline.",
                Api("Corridor.Baselines.Add",
                    "Autodesk.Civil.DatabaseServices",
                    ("corridorId",  "$corridor"),
                    ("alignmentId", "$alignment"),
                    ("profileId",   "$profile"),
                    ("outVar",      "$baseline")));

        private static WorkflowStep CorridorStep_AddRegion(
            WorkflowGeneratorOptions o, double endStation) =>
            MakeStep("Add Corridor Region",
                "Assign the assembly to the corridor baseline for the full design range.",
                "Corridor",
                rollback: "Remove the corridor region.",
                Api("CorridorBaseline.Regions.Add",
                    "Autodesk.Civil.DatabaseServices",
                    ("baselineId",    "$baseline"),
                    ("assemblyId",    "$assembly"),
                    ("startStation",  o.StartStation),
                    ("endStation",    endStation),
                    ("sampleSpacing", 5.0),
                    ("outVar",        "$region")));

        private static WorkflowStep CorridorStep_SetTargets(WorkflowGeneratorOptions o) =>
            MakeStep("Set Targets",
                "Map logical target references (surface, width, offset) to actual objects.",
                "Corridor",
                rollback: "Clear target mappings.",
                Api("CorridorRegion.SetTargets",
                    "Autodesk.Civil.DatabaseServices",
                    ("regionId",       "$region"),
                    ("surfaceTarget",  o.ExistingSurface),
                    ("widthTarget",    ""),
                    ("outVar",         null!)),
                isCritical: false);

        private static WorkflowStep CorridorStep_Rebuild(WorkflowGeneratorOptions o) =>
            MakeStep("Rebuild Corridor",
                "Rebuild the corridor model to apply all baselines, regions, and targets.",
                "Corridor",
                rollback: null,
                Api("Corridor.Rebuild",
                    "Autodesk.Civil.DatabaseServices",
                    ("corridorId", "$corridor")));

        private static WorkflowStep CorridorStep_ApplyLabelStyles(WorkflowGeneratorOptions o) =>
            MakeStep("Apply Label Styles",
                "Apply station/offset and slope label styles to the corridor.",
                "Labels",
                rollback: null,
                Api("Corridor.ApplyLabelStyles",
                    "Autodesk.Civil.DatabaseServices",
                    ("corridorId", "$corridor"),
                    ("styleSet",   "Standard")),
                isCritical: false);

        private static WorkflowStep CorridorStep_ExportReport(WorkflowGeneratorOptions o) =>
            MakeStep("Export Report",
                $"Export corridor summary to '{o.ReportOutputPath}'.",
                "Report",
                rollback: $"Delete file '{o.ReportOutputPath}'.",
                Api("CorridorReport.Export",
                    "Autodesk.Civil.DatabaseServices",
                    ("corridorId",  "$corridor"),
                    ("outputPath",  o.ReportOutputPath),
                    ("format",      "CSV")),
                isCritical: false);

        // ─────────────────────────────────────────────────────────────────────
        // Pipe network step builders
        // ─────────────────────────────────────────────────────────────────────

        private static WorkflowStep PipeStep_CreateNetwork(
            WorkflowGeneratorOptions o, string partList) =>
            MakeStep("Create Pipe Network",
                $"Create a new {o.NetworkType.ToLower()} pipe network '{o.NetworkName}'.",
                "PipeNetwork",
                rollback: $"Delete pipe network '{o.NetworkName}'.",
                Api("PipeNetworkCollection.Add",
                    "Autodesk.Civil.DatabaseServices",
                    ("name",            o.NetworkName),
                    ("networkType",     o.NetworkType),
                    ("partsList",       partList),
                    ("pipeStyle",       "Standard"),
                    ("structureStyle",  "Standard"),
                    ("outVar",          "$network")));

        private static WorkflowStep PipeStep_SetLayoutRules(WorkflowGeneratorOptions o) =>
            MakeStep("Set Layout Rules",
                "Configure minimum/maximum slope, diameter, and cover depth rules.",
                "PipeNetwork",
                rollback: "Reset layout rules to defaults.",
                Api("PipeNetwork.SetLayoutRules",
                    "Autodesk.Civil.DatabaseServices",
                    ("networkId",     "$network"),
                    ("minSlope_pct",  o.MinSlope_pct),
                    ("maxSlope_pct",  o.MaxSlope_pct),
                    ("minCover_m",    o.MinCoverDepth_m),
                    ("maxCover_m",    o.MaxCoverDepth_m),
                    ("standard",      o.StandardReference)));

        private static WorkflowStep PipeStep_LayoutPipes(WorkflowGeneratorOptions o) =>
            MakeStep("Layout Pipes",
                $"Lay out pipes with diameter {o.PipeDiameter_mm} mm along the design route.",
                "PipeNetwork",
                rollback: "Delete all pipes in network.",
                Api("PipeNetwork.DrawPipes",
                    "Autodesk.Civil.DatabaseServices",
                    ("networkId",   "$network"),
                    ("diameter_mm", o.PipeDiameter_mm),
                    ("routeSource", "AlignmentAndProfile"),
                    ("alignmentId", "$alignment"),
                    ("profileId",   "$profile")));

        private static WorkflowStep PipeStep_CreateStructures(WorkflowGeneratorOptions o) =>
            MakeStep("Create Structures",
                "Insert manholes/catch basins at pipe junctions and endpoints.",
                "PipeNetwork",
                rollback: "Delete all structures in network.",
                Api("PipeNetwork.InsertStructures",
                    "Autodesk.Civil.DatabaseServices",
                    ("networkId",      "$network"),
                    ("structurePart",  o.NetworkType == "Storm"
                        ? "Catch Basin 24 inch" : "Sanitary Manhole 48 inch"),
                    ("placementMode",  "AtJunctions")));

        private static WorkflowStep PipeStep_SetReferenceAlignment(WorkflowGeneratorOptions o) =>
            MakeStep("Set Reference Alignment",
                $"Assign alignment '{o.AlignmentName}' as the horizontal reference for the network.",
                "PipeNetwork",
                rollback: "Clear reference alignment.",
                Api("PipeNetwork.SetReferenceAlignment",
                    "Autodesk.Civil.DatabaseServices",
                    ("networkId",   "$network"),
                    ("alignmentId", "$alignment")),
                isCritical: false);

        private static WorkflowStep PipeStep_AssignPipeToSurface(WorkflowGeneratorOptions o) =>
            MakeStep("Assign to Surface",
                $"Link pipe cover calculations to surface '{o.ExistingSurface}'.",
                "PipeNetwork",
                rollback: "Clear surface reference.",
                Api("PipeNetwork.SetReferenceSurface",
                    "Autodesk.Civil.DatabaseServices",
                    ("networkId", "$network"),
                    ("surfaceName", o.ExistingSurface)));

        private static WorkflowStep PipeStep_ApplyLabelStyles(WorkflowGeneratorOptions o) =>
            MakeStep("Apply Label Styles",
                "Apply pipe/structure label styles to the network.",
                "Labels",
                rollback: null,
                Api("PipeNetwork.ApplyLabelStyles",
                    "Autodesk.Civil.DatabaseServices",
                    ("networkId",         "$network"),
                    ("pipeLabelStyle",    $"{o.NetworkType} Pipe Label"),
                    ("structLabelStyle",  $"{o.NetworkType} Structure Label")),
                isCritical: false);

        private static WorkflowStep PipeStep_ValidateCoverDepths(WorkflowGeneratorOptions o) =>
            MakeStep("Validate Cover Depths",
                $"Check all pipes meet minimum cover depth ({o.MinCoverDepth_m} m) per {o.StandardReference}.",
                "Validation",
                rollback: null,
                Api("PipeNetworkValidator.CheckCoverDepths",
                    "Autodesk.Civil.DatabaseServices",
                    ("networkId",  "$network"),
                    ("minCover_m", o.MinCoverDepth_m),
                    ("maxCover_m", o.MaxCoverDepth_m),
                    ("outVar",     "$coverViolations")));

        private static WorkflowStep PipeStep_ValidateSlopes(WorkflowGeneratorOptions o) =>
            MakeStep("Validate Slopes",
                $"Check all pipes meet slope requirements per {o.StandardReference}.",
                "Validation",
                rollback: null,
                Api("PipeNetworkValidator.CheckSlopes",
                    "Autodesk.Civil.DatabaseServices",
                    ("networkId",    "$network"),
                    ("minSlope_pct", o.MinSlope_pct),
                    ("maxSlope_pct", o.MaxSlope_pct),
                    ("outVar",       "$slopeViolations")));

        private static WorkflowStep PipeStep_ExportReport(WorkflowGeneratorOptions o) =>
            MakeStep("Export Report",
                $"Export pipe network validation report to '{o.ReportOutputPath}'.",
                "Report",
                rollback: null,
                Api("PipeNetworkReport.Export",
                    "Autodesk.Civil.DatabaseServices",
                    ("networkId",   "$network"),
                    ("outputPath",  o.ReportOutputPath),
                    ("format",      "CSV"),
                    ("includeViolations", true)),
                isCritical: false);

        // ─────────────────────────────────────────────────────────────────────
        // Site grading step builders
        // ─────────────────────────────────────────────────────────────────────

        private static WorkflowStep GradeStep_GetExistingSurface(WorkflowGeneratorOptions o) =>
            MakeStep("Get Existing Surface",
                $"Retrieve existing ground surface '{o.ExistingSurface}'.",
                "Surface",
                rollback: null,
                Api("CivilDocument.GetSurfaceByName",
                    "Autodesk.Civil.ApplicationServices",
                    ("name",   o.ExistingSurface),
                    ("outVar", "$existingSurface")));

        private static WorkflowStep GradeStep_CreateGradingGroup(WorkflowGeneratorOptions o) =>
            MakeStep("Create Grading Group",
                "Create a grading group to contain the grading objects.",
                "Grading",
                rollback: "Delete grading group.",
                Api("GradingGroupCollection.Add",
                    "Autodesk.Civil.DatabaseServices",
                    ("name",      $"{o.ExistingSurface}_GradingGroup"),
                    ("siteId",    "ActiveSite"),
                    ("outVar",    "$gradingGroup")));

        private static WorkflowStep GradeStep_SetGradingCriteria(WorkflowGeneratorOptions o) =>
            MakeStep("Set Grading Criteria",
                $"Apply cut slope {o.CutSlope}:1 and fill slope {o.FillSlope}:1.",
                "Grading",
                rollback: "Reset grading criteria to defaults.",
                Api("GradingGroup.SetCriteria",
                    "Autodesk.Civil.DatabaseServices",
                    ("groupId",    "$gradingGroup"),
                    ("cutSlope",   o.CutSlope),
                    ("fillSlope",  o.FillSlope),
                    ("offset_m",   o.GradingOffset_m),
                    ("standard",   o.StandardReference)));

        private static WorkflowStep GradeStep_CreateGradingObject(WorkflowGeneratorOptions o) =>
            MakeStep("Create Grading Object",
                "Create the grading feature from the selected footprint feature line.",
                "Grading",
                rollback: "Delete grading object.",
                Api("GradingGroup.AddGrading",
                    "Autodesk.Civil.DatabaseServices",
                    ("groupId",          "$gradingGroup"),
                    ("baseFeatureLine",  "FootprintFeatureLine"),
                    ("targetSurfaceId",  "$existingSurface"),
                    ("outVar",           "$grading")));

        private static WorkflowStep GradeStep_CreateFinishedSurface(WorkflowGeneratorOptions o) =>
            MakeStep("Create Finished Surface",
                $"Generate finished grade surface '{o.FinishedSurface}' from grading.",
                "Surface",
                rollback: $"Delete surface '{o.FinishedSurface}'.",
                Api("GradingGroup.CreateSurface",
                    "Autodesk.Civil.DatabaseServices",
                    ("groupId",       "$gradingGroup"),
                    ("surfaceName",   o.FinishedSurface),
                    ("outVar",        "$finishedSurface")));

        private static WorkflowStep GradeStep_CreateVolumeSurface(WorkflowGeneratorOptions o) =>
            MakeStep("Create TIN Volume Surface",
                "Create a TIN volume surface from existing and finished grade surfaces.",
                "Surface",
                rollback: "Delete TIN volume surface.",
                Api("TinVolumeSurface.Create",
                    "Autodesk.Civil.DatabaseServices",
                    ("name",           $"Vol_{o.ExistingSurface}_{o.FinishedSurface}"),
                    ("baseSurfaceId",  "$existingSurface"),
                    ("compSurfaceId",  "$finishedSurface"),
                    ("outVar",         "$volumeSurface")));

        private static WorkflowStep GradeStep_ExtractContours(WorkflowGeneratorOptions o) =>
            MakeStep("Extract Finished Contours",
                "Extract contour lines from the finished grade surface.",
                "Surface",
                rollback: "Delete extracted contour entities.",
                Api("TinSurface.ExtractContours",
                    "Autodesk.Civil.DatabaseServices",
                    ("surfaceId",       "$finishedSurface"),
                    ("minorInterval_m", 0.5),
                    ("majorInterval_m", 2.5)),
                isCritical: false);

        private static WorkflowStep GradeStep_CalculateVolumes(WorkflowGeneratorOptions o) =>
            MakeStep("Calculate Cut/Fill Volumes",
                "Read net cut and fill volumes from the TIN volume surface.",
                "Volume",
                rollback: null,
                Api("TinVolumeSurface.GetCutFillVolumes",
                    "Autodesk.Civil.DatabaseServices",
                    ("surfaceId",   "$volumeSurface"),
                    ("outCut_m3",   "$cutVolume"),
                    ("outFill_m3",  "$fillVolume")));

        private static WorkflowStep GradeStep_ExportReport(WorkflowGeneratorOptions o) =>
            MakeStep("Export Volume Report",
                $"Export cut/fill volume report to '{o.ReportOutputPath}'.",
                "Report",
                rollback: null,
                Api("VolumeReport.Export",
                    "Autodesk.Civil.DatabaseServices",
                    ("volumeSurfaceId", "$volumeSurface"),
                    ("outputPath",      o.ReportOutputPath),
                    ("format",          "CSV")),
                isCritical: false);

        // ─────────────────────────────────────────────────────────────────────
        // Road design step builders
        // ─────────────────────────────────────────────────────────────────────

        private static WorkflowStep RoadStep_CreateAlignment(WorkflowGeneratorOptions o) =>
            MakeStep("Create Horizontal Alignment",
                $"Create alignment '{o.AlignmentName}' at design speed {o.DesignSpeed_kmh} km/h.",
                "Alignment",
                rollback: $"Delete alignment '{o.AlignmentName}'.",
                Api("AlignmentCollection.AddFromPolyline",
                    "Autodesk.Civil.DatabaseServices",
                    ("name",           o.AlignmentName),
                    ("siteId",         "ActiveSite"),
                    ("designSpeed",    o.DesignSpeed_kmh),
                    ("alignStyle",     "Standard"),
                    ("outVar",         "$alignment")));

        private static WorkflowStep RoadStep_SampleExistingProfile(WorkflowGeneratorOptions o) =>
            MakeStep("Sample Existing Ground Profile",
                $"Create an existing ground profile by sampling surface '{o.ExistingSurface}'.",
                "Profile",
                rollback: "Delete existing ground profile.",
                Api("Profile.CreateFromSurface",
                    "Autodesk.Civil.DatabaseServices",
                    ("alignmentId",   "$alignment"),
                    ("surfaceId",     "$existingSurface"),
                    ("profileName",   "Existing Ground Profile"),
                    ("outVar",        "$existingProfile")));

        private static WorkflowStep RoadStep_CreateDesignProfile(WorkflowGeneratorOptions o) =>
            MakeStep("Create Design Profile",
                $"Create empty design profile '{o.ProfileName}' on the alignment.",
                "Profile",
                rollback: $"Delete design profile '{o.ProfileName}'.",
                Api("ProfilePVICollection.Create",
                    "Autodesk.Civil.DatabaseServices",
                    ("alignmentId",  "$alignment"),
                    ("name",         o.ProfileName),
                    ("profileStyle", "Design Profile"),
                    ("outVar",       "$designProfile")));

        private static WorkflowStep RoadStep_SetVerticalGeometry(WorkflowGeneratorOptions o) =>
            MakeStep("Set Vertical Geometry",
                "Insert PVIs and set vertical curve lengths per design speed.",
                "Profile",
                rollback: "Remove inserted PVIs.",
                Api("Profile.SetVerticalCurveData",
                    "Autodesk.Civil.DatabaseServices",
                    ("profileId",        "$designProfile"),
                    ("designSpeed_kmh",  o.DesignSpeed_kmh),
                    ("standard",         o.StandardReference)));

        private static WorkflowStep RoadStep_CreateAssembly(WorkflowGeneratorOptions o) =>
            MakeStep("Create Road Assembly",
                $"Create assembly '{o.AssemblyName}' at the origin.",
                "Assembly",
                rollback: $"Delete assembly '{o.AssemblyName}'.",
                Api("AssemblyCollection.Add",
                    "Autodesk.Civil.DatabaseServices",
                    ("name",     o.AssemblyName),
                    ("outVar",   "$assembly")));

        private static WorkflowStep RoadStep_AddSubassemblies(WorkflowGeneratorOptions o) =>
            MakeStep("Add Subassemblies",
                $"Attach travel lanes ({o.LaneWidth_m} m × {o.NumberOfLanes}), curbs, and sidewalks.",
                "Assembly",
                rollback: "Remove subassemblies from assembly.",
                Api("Assembly.AddSubassembly",
                    "Autodesk.Civil.DatabaseServices",
                    ("assemblyId",   "$assembly"),
                    ("laneWidth_m",  o.LaneWidth_m),
                    ("numLanes",     o.NumberOfLanes),
                    ("rowWidth_m",   o.RightOfWay_m),
                    ("subassemblyToolPalette", "Road Lanes - Imperial")));

        private static WorkflowStep RoadStep_CreateCorridor(WorkflowGeneratorOptions o) =>
            MakeStep("Create Road Corridor",
                $"Create corridor '{o.CorridorName}' from alignment, design profile, and assembly.",
                "Corridor",
                rollback: $"Delete corridor '{o.CorridorName}'.",
                Api("CorridorCollection.Add",
                    "Autodesk.Civil.DatabaseServices",
                    ("name",        o.CorridorName),
                    ("alignmentId", "$alignment"),
                    ("profileId",   "$designProfile"),
                    ("assemblyId",  "$assembly"),
                    ("outVar",      "$corridor")));

        private static WorkflowStep RoadStep_CreateSampleLines(WorkflowGeneratorOptions o) =>
            MakeStep("Create Sample Lines",
                "Generate cross-section sample lines every 20 m along the corridor.",
                "SampleLines",
                rollback: "Delete sample line group.",
                Api("SampleLineGroupCollection.Add",
                    "Autodesk.Civil.DatabaseServices",
                    ("alignmentId",  "$alignment"),
                    ("spacing_m",    20.0),
                    ("swathLeft_m",  o.RightOfWay_m / 2.0),
                    ("swathRight_m", o.RightOfWay_m / 2.0),
                    ("outVar",       "$sampleLineGroup")));

        private static WorkflowStep RoadStep_CreateCrossSections(WorkflowGeneratorOptions o) =>
            MakeStep("Create Cross Sections",
                "Generate cross-section sheets for all sample lines.",
                "CrossSection",
                rollback: "Delete cross-section view group.",
                Api("SectionViewGroup.Create",
                    "Autodesk.Civil.DatabaseServices",
                    ("sampleGroupId",   "$sampleLineGroup"),
                    ("plotSheetStyle",  "Standard"),
                    ("outVar",          "$sectionViews")));

        private static WorkflowStep RoadStep_GenerateQuantities(WorkflowGeneratorOptions o) =>
            MakeStep("Generate Quantity Takeoff",
                "Compute earthwork and pavement material quantities from cross-sections.",
                "Quantities",
                rollback: null,
                Api("MaterialList.Compute",
                    "Autodesk.Civil.DatabaseServices",
                    ("corridorId",    "$corridor"),
                    ("sampleGroupId", "$sampleLineGroup"),
                    ("outVar",        "$materialList")));

        private static WorkflowStep RoadStep_ExportQuantities(WorkflowGeneratorOptions o) =>
            MakeStep("Export Quantity Report",
                $"Export material quantity report to '{o.ReportOutputPath}'.",
                "Report",
                rollback: null,
                Api("QuantityReport.Export",
                    "Autodesk.Civil.DatabaseServices",
                    ("materialListId", "$materialList"),
                    ("outputPath",     o.ReportOutputPath),
                    ("format",         "CSV"),
                    ("includeUnits",   true)),
                isCritical: false);

        // ─────────────────────────────────────────────────────────────────────
        // Utility helpers
        // ─────────────────────────────────────────────────────────────────────

        private static WorkflowStep MakeStep(
            string name,
            string description,
            string category,
            string? rollback,
            WorkflowApiCall call,
            bool isCritical = true,
            int durationSeconds = 5)
        {
            return new WorkflowStep
            {
                Name                = name,
                Description         = description,
                Category            = category,
                ApiCalls            = new[] { call },
                RollbackProcedure   = rollback,
                IsCritical          = isCritical,
                EstimatedDuration   = TimeSpan.FromSeconds(durationSeconds),
                RequiresTransaction = true
            };
        }

        private static WorkflowApiCall Api(
            string method,
            string ns,
            params (string Name, object Value)[] parameters)
        {
            var pList = parameters
                .Where(p => p.Name != "outVar")
                .Select(p => (p.Name, p.Value))
                .ToList()
                .AsReadOnly();

            var outEntry = parameters.FirstOrDefault(p => p.Name == "outVar");
            string? outVar = outEntry.Value?.ToString();

            return new WorkflowApiCall
            {
                ApiMethod   = method,
                Namespace   = ns,
                Parameters  = pList,
                OutVariable = string.IsNullOrEmpty(outVar) ? null : outVar,
                RequiresTransaction = true
            };
        }

        private static void RenumberSteps(List<WorkflowStep> steps)
        {
            // WorkflowStep.StepNumber is init-only; we construct new copies.
            for (int i = 0; i < steps.Count; i++)
            {
                // Reflection-free approach: replace via positional slot in list.
                var s = steps[i];
                steps[i] = new WorkflowStep
                {
                    StepNumber          = i + 1,
                    StepId              = s.StepId,
                    Name                = s.Name,
                    Description         = s.Description,
                    Category            = s.Category,
                    ApiCalls            = s.ApiCalls,
                    RollbackProcedure   = s.RollbackProcedure,
                    RequiresTransaction = s.RequiresTransaction,
                    IsCritical          = s.IsCritical,
                    EstimatedDuration   = s.EstimatedDuration
                };
            }
        }

        private static IReadOnlyList<string> RequiredDlls(params string[] modules)
        {
            var dlls = new List<string>
            {
                "Autodesk.Civil.ApplicationServices",
                "Autodesk.Civil.DatabaseServices",
                "AeccXUiLand",
                "AcMgd",
                "AcDbMgd",
                "AcCoreMgd"
            };

            if (modules.Contains("roadway", StringComparer.OrdinalIgnoreCase))
                dlls.Add("AeccXUiRoadway");

            if (modules.Contains("pipe", StringComparer.OrdinalIgnoreCase))
                dlls.Add("AeccXUiPipe");

            if (modules.Contains("grading", StringComparer.OrdinalIgnoreCase))
                dlls.Add("AeccXUiGrading");

            return dlls.Distinct().ToList().AsReadOnly();
        }
    }
}
