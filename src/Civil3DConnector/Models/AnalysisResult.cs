// ============================================================================
// AnalysisResult.cs
// Civil 3D DWG Analysis – Core Data Models
// Compatible with Autodesk Civil 3D 2025 / 2026 / 2027
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace Civil3DConnector.Models
{
    // -------------------------------------------------------------------------
    // Enumerations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Severity level assigned to every detected issue. Ordering is intentional:
    /// higher numeric value = higher severity so callers can compare easily.
    /// </summary>
    public enum IssueSeverity
    {
        /// <summary>Informational note – no action required.</summary>
        Info = 0,

        /// <summary>Minor issue that should be reviewed; unlikely to cause failures.</summary>
        Warning = 1,

        /// <summary>
        /// Serious problem that will cause data loss, processing errors, or export
        /// failures if left unresolved.
        /// </summary>
        Critical = 2
    }

    /// <summary>
    /// Logical grouping of issues to allow filtered views in reports and dashboards.
    /// </summary>
    public enum IssueCategory
    {
        /// <summary>Object geometry (coordinates, TIN edges, PI tangency …).</summary>
        Geometry,

        /// <summary>Broken object references, missing XREFs, dangling handles.</summary>
        Reference,

        /// <summary>Missing or broken label / band styles.</summary>
        Style,

        /// <summary>Coordinate system or projection mismatch.</summary>
        CoordinateSystem,

        /// <summary>Data Shortcuts not found or stale.</summary>
        DataShortcut,

        /// <summary>
        /// Pipe or pressure-network issues (slope, cover, fitting catalog).
        /// </summary>
        Network,

        /// <summary>Surface-specific problems (TIN, boundaries, breaklines).</summary>
        Surface,

        /// <summary>Corridor assembly, baseline or region problems.</summary>
        Corridor,

        /// <summary>Profile grade, K-value or vertical curve issues.</summary>
        Profile,

        /// <summary>Alignment stationing, tangent/curve geometry, PI list.</summary>
        Alignment,

        /// <summary>Orphaned or disconnected feature lines.</summary>
        FeatureLine,

        /// <summary>General database / object-header corruption.</summary>
        Corruption,

        /// <summary>Catch-all for miscellaneous issues.</summary>
        General
    }

    /// <summary>
    /// Civil 3D object type. Values map to the managed-API class hierarchy used
    /// during validation so that callers can route issues to the correct validator.
    /// </summary>
    public enum ObjectType
    {
        Unknown,
        Alignment,
        Profile,
        ProfileView,
        Corridor,
        Surface,
        TinSurface,
        GridSurface,
        PipeNetwork,
        PressureNetwork,
        Structure,
        Pipe,
        PressurePipe,
        PressureFitting,
        Assembly,
        Subassembly,
        FeatureLine,
        Grading,
        SampleLineGroup,
        SectionView,
        PointGroup,
        CogoPoint,
        DataShortcut,
        LabelStyle,
        XRef,
        BlockReference,
        Layer
    }

    // -------------------------------------------------------------------------
    // Fix suggestion
    // -------------------------------------------------------------------------

    /// <summary>
    /// Human-readable suggestion attached to an <see cref="ObjectIssue"/> that
    /// describes how a user or automated process can resolve the problem.
    /// </summary>
    public sealed class FixSuggestion
    {
        /// <summary>Short one-liner displayed in summary views.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Detailed paragraph explaining the repair steps.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Whether this fix can potentially be applied programmatically by the
        /// analysis tooling. <c>false</c> means the user must act manually.
        /// </summary>
        public bool IsAutomatable { get; set; }

        /// <summary>
        /// Optional .NET type name of the repair action class that implements the
        /// fix when <see cref="IsAutomatable"/> is <c>true</c>.
        /// </summary>
        public string? RepairActionType { get; set; }

        /// <summary>
        /// Estimated effort (in minutes) for a skilled Civil 3D operator to apply
        /// the fix manually. Used to prioritise issue queues.
        /// </summary>
        public int EstimatedManualMinutes { get; set; }

        /// <inheritdoc/>
        public override string ToString() => Title;
    }

    // -------------------------------------------------------------------------
    // Individual issue record
    // -------------------------------------------------------------------------

    /// <summary>
    /// Represents a single problem detected during DWG analysis. Each issue is
    /// associated with a specific Civil 3D object and carries enough context for
    /// both human review and automated processing.
    /// </summary>
    public sealed class ObjectIssue
    {
        /// <summary>Unique identifier for this issue within the current analysis run.</summary>
        public Guid IssueId { get; } = Guid.NewGuid();

        /// <summary>AutoCAD object handle (hex string, e.g. "1A3F").</summary>
        public string ObjectHandle { get; set; } = string.Empty;

        /// <summary>Display name of the object (e.g. the alignment name).</summary>
        public string ObjectName { get; set; } = string.Empty;

        /// <summary>Civil 3D / AutoCAD type of the affected object.</summary>
        public ObjectType ObjectType { get; set; }

        /// <summary>
        /// Fully qualified AutoCAD RXClass name (e.g.
        /// <c>Autodesk.Civil.DatabaseServices.Alignment</c>).
        /// </summary>
        public string RxClassName { get; set; } = string.Empty;

        /// <summary>Severity of this issue.</summary>
        public IssueSeverity Severity { get; set; }

        /// <summary>Category that groups this issue with related problems.</summary>
        public IssueCategory Category { get; set; }

        /// <summary>Short message (≤120 chars) shown in list views.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Full technical description including measured values.</summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>
        /// Zero-based entity index within its parent collection (e.g. segment index
        /// in an alignment), if applicable.
        /// </summary>
        public int? ElementIndex { get; set; }

        /// <summary>Station value along an alignment or profile, if applicable.</summary>
        public double? Station { get; set; }

        /// <summary>
        /// Layer name of the affected object.
        /// </summary>
        public string LayerName { get; set; } = string.Empty;

        /// <summary>Timestamp when the issue was recorded.</summary>
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Ordered list of recommended fixes. The first entry is the preferred
        /// approach; subsequent entries are alternatives.
        /// </summary>
        public List<FixSuggestion> FixSuggestions { get; set; } = new List<FixSuggestion>();

        /// <summary>
        /// Arbitrary key/value pairs for additional context that does not fit the
        /// standard fields (e.g. measured slope, expected value, catalog path …).
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc/>
        public override string ToString() =>
            $"[{Severity}] {ObjectType} '{ObjectName}' ({ObjectHandle}): {Message}";
    }

    // -------------------------------------------------------------------------
    // Summary statistics
    // -------------------------------------------------------------------------

    /// <summary>
    /// Aggregated statistics computed from the full issue list at the end of an
    /// analysis run. Immutable after construction.
    /// </summary>
    public sealed class AnalysisSummary
    {
        /// <summary>Total number of issues across all severity levels.</summary>
        public int TotalIssues { get; init; }

        /// <summary>Number of <see cref="IssueSeverity.Critical"/> issues.</summary>
        public int CriticalCount { get; init; }

        /// <summary>Number of <see cref="IssueSeverity.Warning"/> issues.</summary>
        public int WarningCount { get; init; }

        /// <summary>Number of <see cref="IssueSeverity.Info"/> issues.</summary>
        public int InfoCount { get; init; }

        /// <summary>Number of unique Civil 3D objects that have at least one issue.</summary>
        public int AffectedObjectCount { get; init; }

        /// <summary>Issue counts broken down by <see cref="IssueCategory"/>.</summary>
        public IReadOnlyDictionary<IssueCategory, int> ByCategory { get; init; }
            = new Dictionary<IssueCategory, int>();

        /// <summary>Issue counts broken down by <see cref="ObjectType"/>.</summary>
        public IReadOnlyDictionary<ObjectType, int> ByObjectType { get; init; }
            = new Dictionary<ObjectType, int>();

        /// <summary>
        /// Overall health score (0–100). 100 = no issues; each issue deducts
        /// weighted points (Critical = 10, Warning = 3, Info = 1) capped at zero.
        /// </summary>
        public int HealthScore { get; init; }

        /// <summary>Human-readable health tier derived from <see cref="HealthScore"/>.</summary>
        public string HealthTier =>
            HealthScore >= 90 ? "Excellent" :
            HealthScore >= 70 ? "Good" :
            HealthScore >= 50 ? "Fair" :
            HealthScore >= 25 ? "Poor" : "Critical";
    }

    // -------------------------------------------------------------------------
    // Object inventory record
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lightweight inventory entry recorded for every Civil 3D object encountered
    /// during scanning, regardless of whether it has issues.
    /// </summary>
    public sealed class ObjectRecord
    {
        /// <summary>AutoCAD object handle (hex).</summary>
        public string Handle { get; set; } = string.Empty;

        /// <summary>Display name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Civil 3D object type.</summary>
        public ObjectType Type { get; set; }

        /// <summary>AutoCAD layer name.</summary>
        public string Layer { get; set; } = string.Empty;

        /// <summary>Number of issues associated with this object.</summary>
        public int IssueCount { get; set; }

        /// <summary>Highest severity among associated issues, or null if clean.</summary>
        public IssueSeverity? MaxSeverity { get; set; }

        /// <summary>
        /// Additional type-specific metadata (e.g. alignment length, surface area).
        /// </summary>
        public Dictionary<string, string> Properties { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Top-level result container
    // -------------------------------------------------------------------------

    /// <summary>
    /// Root result object returned by <c>DwgAnalyzer.AnalyzeAsync</c>. Contains
    /// the full issue list, object inventory, summary statistics, and analysis
    /// metadata for a single DWG file inspection run.
    /// </summary>
    public sealed class AnalysisResult
    {
        // ------------------------------------------------------------------
        // Identification / metadata
        // ------------------------------------------------------------------

        /// <summary>Unique identifier for this analysis run.</summary>
        public Guid RunId { get; } = Guid.NewGuid();

        /// <summary>UTC timestamp when the analysis started.</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>UTC timestamp when the analysis completed.</summary>
        public DateTime CompletedAt { get; set; }

        /// <summary>Wall-clock duration of the analysis.</summary>
        public TimeSpan Duration => CompletedAt - StartedAt;

        /// <summary>Full path to the DWG file that was analysed.</summary>
        public string DwgFilePath { get; set; } = string.Empty;

        /// <summary>File size in bytes (0 if the file could not be measured).</summary>
        public long FileSizeBytes { get; set; }

        /// <summary>AutoCAD drawing version string (e.g. "AC1032" = R2018+).</summary>
        public string DrawingVersion { get; set; } = string.Empty;

        /// <summary>Civil 3D version found in the drawing custom properties.</summary>
        public string Civil3DVersion { get; set; } = string.Empty;

        /// <summary>
        /// Drawing coordinate system code (e.g. "UTM84-16N") or empty if not set.
        /// </summary>
        public string CoordinateSystem { get; set; } = string.Empty;

        /// <summary>
        /// Version of the analysis engine that produced this result.
        /// </summary>
        public string AnalyzerVersion { get; set; } = "1.0.0";

        /// <summary>
        /// Whether the analysis completed without unhandled exceptions.
        /// A <c>false</c> value means <see cref="FatalError"/> is populated.
        /// </summary>
        public bool IsSuccessful { get; set; } = true;

        /// <summary>
        /// Error message when the analysis could not complete (<see cref="IsSuccessful"/>
        /// = <c>false</c>).
        /// </summary>
        public string? FatalError { get; set; }

        // ------------------------------------------------------------------
        // Findings
        // ------------------------------------------------------------------

        /// <summary>All issues detected during the analysis run.</summary>
        public List<ObjectIssue> Issues { get; set; } = new List<ObjectIssue>();

        /// <summary>
        /// Inventory of every Civil 3D object encountered, keyed by handle.
        /// </summary>
        public List<ObjectRecord> ObjectInventory { get; set; } = new List<ObjectRecord>();

        // ------------------------------------------------------------------
        // Statistics (computed via ComputeSummary)
        // ------------------------------------------------------------------

        /// <summary>
        /// Aggregated statistics. Call <see cref="ComputeSummary"/> to populate
        /// after all issues have been added.
        /// </summary>
        public AnalysisSummary? Summary { get; private set; }

        // ------------------------------------------------------------------
        // Convenience accessors
        // ------------------------------------------------------------------

        /// <summary>Returns only issues with <see cref="IssueSeverity.Critical"/>.</summary>
        public IEnumerable<ObjectIssue> CriticalIssues =>
            Issues.Where(i => i.Severity == IssueSeverity.Critical);

        /// <summary>Returns only issues with <see cref="IssueSeverity.Warning"/>.</summary>
        public IEnumerable<ObjectIssue> Warnings =>
            Issues.Where(i => i.Severity == IssueSeverity.Warning);

        /// <summary>Returns only informational issues.</summary>
        public IEnumerable<ObjectIssue> InfoItems =>
            Issues.Where(i => i.Severity == IssueSeverity.Info);

        /// <summary>Returns issues filtered by category.</summary>
        public IEnumerable<ObjectIssue> GetIssuesByCategory(IssueCategory category) =>
            Issues.Where(i => i.Category == category);

        /// <summary>Returns issues filtered by object type.</summary>
        public IEnumerable<ObjectIssue> GetIssuesByObjectType(ObjectType type) =>
            Issues.Where(i => i.ObjectType == type);

        // ------------------------------------------------------------------
        // Factory / helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Computes and stores the <see cref="Summary"/> from the current
        /// <see cref="Issues"/> list.  Must be called once all issues have been
        /// added (typically at the end of the analysis run).
        /// </summary>
        public AnalysisSummary ComputeSummary()
        {
            int critical = Issues.Count(i => i.Severity == IssueSeverity.Critical);
            int warning  = Issues.Count(i => i.Severity == IssueSeverity.Warning);
            int info     = Issues.Count(i => i.Severity == IssueSeverity.Info);

            int affected = Issues
                .Where(i => !string.IsNullOrEmpty(i.ObjectHandle))
                .Select(i => i.ObjectHandle)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var byCategory = Issues
                .GroupBy(i => i.Category)
                .ToDictionary(g => g.Key, g => g.Count());

            var byType = Issues
                .GroupBy(i => i.ObjectType)
                .ToDictionary(g => g.Key, g => g.Count());

            // Health score: deduct weighted penalties, floor at 0.
            int deductions = (critical * 10) + (warning * 3) + (info * 1);
            int health = Math.Max(0, 100 - deductions);

            Summary = new AnalysisSummary
            {
                TotalIssues         = Issues.Count,
                CriticalCount       = critical,
                WarningCount        = warning,
                InfoCount           = info,
                AffectedObjectCount = affected,
                ByCategory          = byCategory,
                ByObjectType        = byType,
                HealthScore         = health
            };

            return Summary;
        }

        /// <summary>
        /// Adds an issue to the <see cref="Issues"/> list and optionally updates
        /// the corresponding <see cref="ObjectRecord"/> in the inventory.
        /// </summary>
        /// <param name="issue">Issue to record.</param>
        public void AddIssue(ObjectIssue issue)
        {
            if (issue is null) throw new ArgumentNullException(nameof(issue));
            Issues.Add(issue);

            // Update inventory entry if it exists.
            if (!string.IsNullOrEmpty(issue.ObjectHandle))
            {
                var record = ObjectInventory.FirstOrDefault(
                    r => string.Equals(r.Handle, issue.ObjectHandle, StringComparison.OrdinalIgnoreCase));

                if (record != null)
                {
                    record.IssueCount++;
                    if (record.MaxSeverity is null || issue.Severity > record.MaxSeverity)
                        record.MaxSeverity = issue.Severity;
                }
            }
        }

        /// <inheritdoc/>
        public override string ToString() =>
            $"AnalysisResult [{RunId:N}] {DwgFilePath} – " +
            $"{Issues.Count} issues ({Issues.Count(i => i.Severity == IssueSeverity.Critical)} critical)";
    }
}
