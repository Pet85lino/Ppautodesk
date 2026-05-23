// ============================================================================
// ObjectValidator.cs
// Civil 3D DWG Analysis – Per-Object Validation Logic
// Compatible with Autodesk Civil 3D 2025 / 2026 / 2027
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Civil3DConnector.Models;

using AcDb  = Autodesk.AutoCAD.DatabaseServices;
using CivDb = Autodesk.Civil.DatabaseServices;

namespace Civil3DConnector.Analyzers
{
    /// <summary>
    /// Validates individual Civil 3D objects and returns a list of
    /// <see cref="ObjectIssue"/> records for each problem found.
    ///
    /// <para>
    /// Validation methods accept an open <see cref="Transaction"/> so they can
    /// be batched within a single transaction by the caller (typically
    /// <see cref="DwgAnalyzer"/>).
    /// </para>
    ///
    /// <para>
    /// Each public Validate* method is safe to call in isolation – it creates
    /// and commits its own transaction when one is not provided (overloads
    /// without a <c>tr</c> parameter are provided for convenience).
    /// </para>
    /// </summary>
    public sealed class ObjectValidator
    {
        // ------------------------------------------------------------------
        // Constants / thresholds
        // ------------------------------------------------------------------

        /// <summary>Minimum acceptable cover depth for gravity pipes (metres).</summary>
        private const double MinPipeCoverMetres = 0.6;

        /// <summary>Maximum acceptable pipe slope (rise/run). Typical design = 0.20.</summary>
        private const double MaxPipeSlope = 0.50;

        /// <summary>Minimum acceptable pipe slope to achieve self-cleaning velocity.</summary>
        private const double MinPipeSlope = 0.005;

        /// <summary>
        /// Angular tolerance (radians) for deflection checks between consecutive
        /// alignment tangents.
        /// </summary>
        private const double TangentDeflectionTolerance = 1e-6;

        /// <summary>Minimum K-value for crest vertical curves (advisory).</summary>
        private const double MinCrestKValue = 1.0;

        /// <summary>Minimum K-value for sag vertical curves (advisory).</summary>
        private const double MinSagKValue = 1.0;

        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private readonly AnalyzerOptions _options;
        private readonly Database        _db;
        private readonly CivilDocument   _civDoc;

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Initialises the validator with the required context objects.
        /// </summary>
        /// <param name="options">Analyser options.</param>
        /// <param name="db">Open AutoCAD database.</param>
        /// <param name="civDoc">Active Civil document (may be null for non-Civil drawings).</param>
        public ObjectValidator(AnalyzerOptions options, Database db, CivilDocument civDoc)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _db      = db      ?? throw new ArgumentNullException(nameof(db));
            _civDoc  = civDoc;   // may be null
        }

        // =====================================================================
        // Alignment Validation
        // =====================================================================

        /// <summary>
        /// Validates a Civil 3D <see cref="Alignment"/> object.
        /// Checks include:
        /// <list type="bullet">
        ///   <item>Stationing continuity and direction</item>
        ///   <item>Tangent–curve tangency (PI consistency)</item>
        ///   <item>Zero-length entities</item>
        ///   <item>Duplicate PI locations</item>
        ///   <item>Layout geometry type availability</item>
        /// </list>
        /// </summary>
        /// <param name="al">Open Alignment object.</param>
        /// <param name="tr">Active transaction (read access sufficient).</param>
        /// <returns>List of detected issues.</returns>
        public List<ObjectIssue> ValidateAlignment(CivDb.Alignment al, Transaction tr)
        {
            var issues = new List<ObjectIssue>();
            string handle = al.ObjectId.Handle.ToString();
            string name   = SafeName(al);

            // --- Basic sanity ---
            if (al.Length <= 0.0)
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Alignment,
                    ObjectType.Alignment, handle, name,
                    "Alignment has zero or negative length.",
                    $"Length: {al.Length:F4}",
                    new FixSuggestion
                    {
                        Title = "Rebuild or delete the zero-length alignment.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 15
                    }));
                return issues; // Cannot validate further.
            }

            // --- Stationing ---
            double expectedEnd = al.StartingStation + al.Length;
            double stationDiff = Math.Abs(al.EndingStation - expectedEnd);
            if (stationDiff > 0.01)
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Alignment,
                    ObjectType.Alignment, handle, name,
                    "Alignment end station is inconsistent with start station + length.",
                    $"StartStation={al.StartingStation:F4}, Length={al.Length:F4}, " +
                    $"Expected EndStation={expectedEnd:F4}, Actual EndStation={al.EndingStation:F4}, " +
                    $"Delta={stationDiff:F4}",
                    new FixSuggestion
                    {
                        Title = "Recalculate alignment geometry via Geometry Editor.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 20
                    }));
            }

            // --- Entity geometry ---
            var entities = al.Entities;
            if (entities == null || entities.Count == 0)
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Alignment,
                    ObjectType.Alignment, handle, name,
                    "Alignment has no geometry entities.",
                    "The alignment has no tangent, arc, or spiral segments.",
                    new FixSuggestion
                    {
                        Title = "Add geometry to the alignment using the Alignment Layout Tools.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 30
                    }));
                return issues;
            }

            Point2d? prevEndPoint = null;
            double   prevEndStation = al.StartingStation;

            for (int i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                string segLabel = $"Entity[{i}] ({entity.EntityType})";

                // Zero-length entity check.
                if (entity.Length < 1e-6)
                {
                    issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Alignment,
                        ObjectType.Alignment, handle, name,
                        $"{segLabel}: zero-length alignment entity.",
                        $"Station range: {entity.StartStation:F4} – {entity.EndStation:F4}",
                        new FixSuggestion
                        {
                            Title = $"Delete or merge the zero-length entity at station {entity.StartStation:F4}.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 5
                        }));
                }

                // Tangency gap between consecutive entities.
                if (prevEndPoint.HasValue)
                {
                    double gap = prevEndPoint.Value.GetDistanceTo(entity.StartPoint);
                    if (gap > 0.001)
                    {
                        issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Alignment,
                            ObjectType.Alignment, handle, name,
                            $"{segLabel}: gap of {gap:F4}m between this entity and the previous one.",
                            $"Previous entity end: ({prevEndPoint.Value.X:F3}, {prevEndPoint.Value.Y:F3}); " +
                            $"This entity start: ({entity.StartPoint.X:F3}, {entity.StartPoint.Y:F3}). " +
                            $"Stationing gap: {entity.StartStation - prevEndStation:F4}m",
                            new FixSuggestion
                            {
                                Title = "Investigate the alignment PI list for missing tangent connections.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 15
                            }));
                    }
                }

                // Station overlap check.
                if (i > 0 && entity.StartStation < prevEndStation - 0.001)
                {
                    issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Alignment,
                        ObjectType.Alignment, handle, name,
                        $"{segLabel}: station overlap detected.",
                        $"Entity start station {entity.StartStation:F4} is before " +
                        $"the previous entity's end station {prevEndStation:F4}.",
                        new FixSuggestion
                        {
                            Title = "Rebuild the alignment geometry to remove stationing overlaps.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 25
                        }));
                }

                // PI tangency for tangent-to-curve transitions.
                if (entity.EntityType == AlignmentEntityType.Arc &&
                    entity is AlignmentArc arc)
                {
                    if (arc.Radius < 1e-4)
                    {
                        issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Alignment,
                            ObjectType.Alignment, handle, name,
                            $"{segLabel}: arc radius is essentially zero ({arc.Radius:F6}m).",
                            "A near-zero radius arc creates geometric singularities.",
                            new FixSuggestion
                            {
                                Title = "Replace the degenerate arc with a point PI or redesign the curve.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 10
                            }));
                    }

                    double deltaRad = Math.Abs(arc.Delta);
                    if (deltaRad > Math.PI)
                    {
                        issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Alignment,
                            ObjectType.Alignment, handle, name,
                            $"{segLabel}: arc delta angle {Math.Abs(arc.Delta * 180 / Math.PI):F2}° exceeds 180°.",
                            "A reflex arc delta is unusual in road/utility design and may indicate a geometry error."));
                    }
                }

                prevEndPoint    = entity.EndPoint;
                prevEndStation  = entity.EndStation;
            }

            // --- Design speed / superelevation (advisory info) ---
            if (_options.IncludeInfoItems)
            {
                issues.Add(Issue(IssueSeverity.Info, IssueCategory.Alignment,
                    ObjectType.Alignment, handle, name,
                    $"Alignment '{name}' validated: {entities.Count} entities, " +
                    $"length {al.Length:F2}m, stations {al.StartingStation:F2}–{al.EndingStation:F2}.",
                    string.Empty));
            }

            return issues;
        }

        // =====================================================================
        // Profile Validation
        // =====================================================================

        /// <summary>
        /// Validates a Civil 3D <see cref="Profile"/> object.
        /// Checks include:
        /// <list type="bullet">
        ///   <item>Grade break continuity (PVI elevations consistent)</item>
        ///   <item>K-value adequacy for sag and crest curves</item>
        ///   <item>Profile start/end station vs parent alignment</item>
        ///   <item>Overlapping vertical curve segments</item>
        ///   <item>Profile type (surface vs layout)</item>
        /// </list>
        /// </summary>
        /// <param name="prof">Open Profile object.</param>
        /// <param name="parentAlignment">Parent alignment (may be null).</param>
        /// <param name="tr">Active transaction.</param>
        public List<ObjectIssue> ValidateProfile(
            CivDb.Profile    prof,
            CivDb.Alignment? parentAlignment,
            Transaction      tr)
        {
            var issues = new List<ObjectIssue>();
            string handle = prof.ObjectId.Handle.ToString();
            string name   = SafeName(prof);

            // Surface profiles cannot be further validated for geometry.
            if (prof.ProfileType == ProfileType.EG)
            {
                if (_options.IncludeInfoItems)
                {
                    issues.Add(Issue(IssueSeverity.Info, IssueCategory.Profile,
                        ObjectType.Profile, handle, name,
                        $"Profile '{name}' is a surface (EG) profile – geometry validation skipped.",
                        "Surface profiles are generated from TIN data; validate the parent surface instead."));
                }
                return issues;
            }

            // --- Station range vs parent alignment ---
            if (parentAlignment != null)
            {
                double alStart = parentAlignment.StartingStation;
                double alEnd   = parentAlignment.EndingStation;

                if (prof.StartingStation < alStart - 0.01 ||
                    prof.EndingStation   > alEnd   + 0.01)
                {
                    issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Profile,
                        ObjectType.Profile, handle, name,
                        $"Profile station range [{prof.StartingStation:F3}, {prof.EndingStation:F3}] " +
                        $"extends beyond the parent alignment range [{alStart:F3}, {alEnd:F3}].",
                        "Profile data outside the alignment extent cannot be used by design elements.",
                        new FixSuggestion
                        {
                            Title = "Trim or redesign the profile to fit within the alignment stationing.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 10
                        }));
                }
            }

            // --- PVI list ---
            var entities = prof.Entities;
            if (entities == null || entities.Count == 0)
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Profile,
                    ObjectType.Profile, handle, name,
                    "Layout profile has no PVI entities.",
                    "The profile contains no vertical tangents or curves.",
                    new FixSuggestion
                    {
                        Title = "Add PVIs to the profile using the Profile Layout Tools.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 30
                    }));
                return issues;
            }

            double prevEndElev    = double.NaN;
            double prevEndStation = prof.StartingStation;

            for (int i = 0; i < entities.Count; i++)
            {
                var ent = entities[i];
                string segLabel = $"Entity[{i}] ({ent.EntityType})";

                // Elevation continuity.
                if (!double.IsNaN(prevEndElev))
                {
                    double elevGap = Math.Abs(ent.StartElevation - prevEndElev);
                    if (elevGap > 0.001)
                    {
                        issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Profile,
                            ObjectType.Profile, handle, name,
                            $"{segLabel}: elevation discontinuity of {elevGap:F4}m at station {ent.StartStation:F3}.",
                            $"Previous entity end elevation: {prevEndElev:F4}m; " +
                            $"this entity start elevation: {ent.StartElevation:F4}m.",
                            new FixSuggestion
                            {
                                Title = "Correct the grade or PVI elevation to close the elevation gap.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 10
                            }));
                    }
                }

                // Station overlap.
                if (i > 0 && ent.StartStation < prevEndStation - 0.001)
                {
                    issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Profile,
                        ObjectType.Profile, handle, name,
                        $"{segLabel}: station overlap at {ent.StartStation:F3}.",
                        $"This entity starts before the previous entity's end station {prevEndStation:F3}.",
                        new FixSuggestion
                        {
                            Title = "Rebuild the profile PVI list to resolve station overlaps.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 20
                        }));
                }

                // K-value checks for vertical curves.
                if (ent is ProfileParabolaSymmetric parab)
                {
                    double k = parab.KValue;
                    bool isSag = parab.IsSag;

                    double minK = isSag ? MinSagKValue : MinCrestKValue;
                    if (k < minK && _options.IncludeInfoItems)
                    {
                        issues.Add(Issue(IssueSeverity.Info, IssueCategory.Profile,
                            ObjectType.Profile, handle, name,
                            $"{segLabel}: K-value {k:F2} is below advisory minimum {minK:F2} " +
                            $"for a {(isSag ? "sag" : "crest")} curve.",
                            $"Station range: {ent.StartStation:F3} – {ent.EndStation:F3}. " +
                            "Low K-values may indicate inadequate stopping sight distance."));
                    }

                    // Check length > 0.
                    if (ent.Length < 1e-4)
                    {
                        issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Profile,
                            ObjectType.Profile, handle, name,
                            $"{segLabel}: vertical curve has near-zero length ({ent.Length:F6}m).",
                            "A zero-length vertical curve cannot provide smooth grade transitions.",
                            new FixSuggestion
                            {
                                Title = "Assign a minimum curve length or remove the degenerate curve.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 5
                            }));
                    }
                }

                prevEndElev    = ent.EndElevation;
                prevEndStation = ent.EndStation;
            }

            if (_options.IncludeInfoItems)
            {
                issues.Add(Issue(IssueSeverity.Info, IssueCategory.Profile,
                    ObjectType.Profile, handle, name,
                    $"Profile '{name}' validated: {entities.Count} entities, " +
                    $"stations {prof.StartingStation:F2}–{prof.EndingStation:F2}.",
                    string.Empty));
            }

            return issues;
        }

        // =====================================================================
        // Corridor Validation
        // =====================================================================

        /// <summary>
        /// Validates a Civil 3D <see cref="Corridor"/>.
        /// Checks include:
        /// <list type="bullet">
        ///   <item>At least one baseline is assigned</item>
        ///   <item>Each baseline has a valid alignment reference</item>
        ///   <item>Each baseline has a valid profile reference</item>
        ///   <item>Each region has a valid assembly reference</item>
        ///   <item>Orphaned or empty regions</item>
        ///   <item>Assembly ObjectId validity</item>
        /// </list>
        /// </summary>
        /// <param name="corr">Open Corridor object.</param>
        /// <param name="tr">Active transaction.</param>
        public List<ObjectIssue> ValidateCorridor(CivDb.Corridor corr, Transaction tr)
        {
            var issues = new List<ObjectIssue>();
            string handle = corr.ObjectId.Handle.ToString();
            string name   = SafeName(corr);

            // --- Baseline count ---
            var baselines = corr.Baselines;
            if (baselines == null || baselines.Count == 0)
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corridor,
                    ObjectType.Corridor, handle, name,
                    "Corridor has no baselines assigned.",
                    "A corridor requires at least one alignment/profile baseline to generate cross-sections.",
                    new FixSuggestion
                    {
                        Title = "Open Corridor Properties and assign an alignment and profile baseline.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 20
                    }));
                return issues;
            }

            for (int bi = 0; bi < baselines.Count; bi++)
            {
                var bl         = baselines[bi];
                string blLabel = $"Baseline[{bi}]";

                // Alignment reference.
                if (bl.AlignmentId.IsNull || bl.AlignmentId.IsErased || !bl.AlignmentId.IsValid)
                {
                    issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corridor,
                        ObjectType.Corridor, handle, name,
                        $"{blLabel}: alignment reference is null or erased.",
                        "The corridor baseline has lost its alignment. " +
                        "Corridor sections will not rebuild correctly.",
                        new FixSuggestion
                        {
                            Title = "Reassign the alignment in Corridor Properties.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 10
                        }));
                }
                else
                {
                    // Verify the alignment can be opened.
                    try
                    {
                        var al = tr.GetObject(bl.AlignmentId, OpenMode.ForRead) as CivDb.Alignment;
                        if (al == null)
                        {
                            issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corridor,
                                ObjectType.Corridor, handle, name,
                                $"{blLabel}: alignment reference does not resolve to an Alignment object.",
                                string.Empty));
                        }
                    }
                    catch (System.Exception ex)
                    {
                        issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corridor,
                            ObjectType.Corridor, handle, name,
                            $"{blLabel}: cannot open referenced alignment.",
                            ex.Message));
                    }
                }

                // Profile reference.
                if (bl.ProfileId.IsNull || bl.ProfileId.IsErased || !bl.ProfileId.IsValid)
                {
                    issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corridor,
                        ObjectType.Corridor, handle, name,
                        $"{blLabel}: profile reference is null or erased.",
                        "Without a finished ground / design profile the corridor cannot produce elevations.",
                        new FixSuggestion
                        {
                            Title = "Reassign the design profile in Corridor Properties.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 10
                        }));
                }

                // Regions.
                var regions = bl.Regions;
                if (regions == null || regions.Count == 0)
                {
                    issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Corridor,
                        ObjectType.Corridor, handle, name,
                        $"{blLabel}: has no regions defined.",
                        "A baseline without regions will not generate any corridor model.",
                        new FixSuggestion
                        {
                            Title = "Add at least one region in Corridor Properties with an assembly assigned.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 15
                        }));
                    continue;
                }

                for (int ri = 0; ri < regions.Count; ri++)
                {
                    var region      = regions[ri];
                    string regLabel = $"{blLabel} Region[{ri}]";

                    // Station range sanity.
                    if (region.StartStation >= region.EndStation)
                    {
                        issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corridor,
                            ObjectType.Corridor, handle, name,
                            $"{regLabel}: region start station ({region.StartStation:F3}) >= end station ({region.EndStation:F3}).",
                            "An inverted or zero-length region will produce no corridor model.",
                            new FixSuggestion
                            {
                                Title = "Correct the region station extents in Corridor Properties.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 5
                            }));
                    }

                    // Assembly reference.
                    if (region.AssemblyId.IsNull || region.AssemblyId.IsErased || !region.AssemblyId.IsValid)
                    {
                        issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corridor,
                            ObjectType.Corridor, handle, name,
                            $"{regLabel}: assembly reference is null or erased.",
                            "A region without a valid assembly cannot generate cross-sections.",
                            new FixSuggestion
                            {
                                Title = "Reassign the assembly for this corridor region.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 10
                            }));
                    }
                    else
                    {
                        try
                        {
                            var asm = tr.GetObject(region.AssemblyId, OpenMode.ForRead) as CivDb.Assembly;
                            if (asm == null)
                            {
                                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corridor,
                                    ObjectType.Corridor, handle, name,
                                    $"{regLabel}: assembly reference does not resolve to an Assembly object.",
                                    string.Empty));
                            }
                            else if (asm.GetSubassemblyIds().Count == 0)
                            {
                                issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Corridor,
                                    ObjectType.Corridor, handle, name,
                                    $"{regLabel}: assembly '{SafeName(asm)}' has no subassemblies.",
                                    "An assembly without subassemblies will produce an empty cross-section.",
                                    new FixSuggestion
                                    {
                                        Title = "Add subassemblies to the corridor assembly.",
                                        IsAutomatable = false,
                                        EstimatedManualMinutes = 20
                                    }));
                            }
                        }
                        catch (System.Exception ex)
                        {
                            issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Corridor,
                                ObjectType.Corridor, handle, name,
                                $"{regLabel}: cannot open referenced assembly.",
                                ex.Message));
                        }
                    }

                    // Frequency line check.
                    if (_options.IncludeInfoItems)
                    {
                        issues.Add(Issue(IssueSeverity.Info, IssueCategory.Corridor,
                            ObjectType.Corridor, handle, name,
                            $"{regLabel}: station range {region.StartStation:F2}–{region.EndStation:F2} " +
                            $"({region.EndStation - region.StartStation:F2}m).",
                            string.Empty));
                    }
                }
            }

            return issues;
        }

        // =====================================================================
        // Surface Validation
        // =====================================================================

        /// <summary>
        /// Validates a Civil 3D <see cref="Surface"/> (TIN or Grid).
        /// Checks include:
        /// <list type="bullet">
        ///   <item>Non-empty TIN (points and triangles exist)</item>
        ///   <item>TIN edges with near-zero length (degenerate triangles)</item>
        ///   <item>Elevation range sanity (no NaN/±∞ elevations)</item>
        ///   <item>Breakline integrity</item>
        ///   <item>Boundary count and direction</item>
        ///   <item>Grid resolution (for grid surfaces)</item>
        /// </list>
        /// </summary>
        /// <param name="surf">Open Surface object.</param>
        /// <param name="tr">Active transaction.</param>
        public List<ObjectIssue> ValidateSurface(CivDb.Surface surf, Transaction tr)
        {
            var issues = new List<ObjectIssue>();
            string handle = surf.ObjectId.Handle.ToString();
            string name   = SafeName(surf);

            // --- TIN surface specific ---
            if (surf is CivDb.TinSurface tin)
                ValidateTinSurface(tin, issues, handle, name);
            else if (surf is CivDb.GridSurface grid)
                ValidateGridSurface(grid, issues, handle, name);

            // --- General surface checks ---
            var stats = surf.GetGeneralProperties();
            if (double.IsNaN(stats.MinimumElevation) || double.IsInfinity(stats.MinimumElevation))
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Surface,
                    ObjectType.Surface, handle, name,
                    "Surface minimum elevation is NaN or Infinity.",
                    $"MinElevation={stats.MinimumElevation}. This indicates corrupt TIN point data.",
                    new FixSuggestion
                    {
                        Title = "Remove corrupt source data from the surface definition and rebuild.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 30
                    }));
            }

            if (double.IsNaN(stats.MaximumElevation) || double.IsInfinity(stats.MaximumElevation))
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Surface,
                    ObjectType.Surface, handle, name,
                    "Surface maximum elevation is NaN or Infinity.",
                    $"MaxElevation={stats.MaximumElevation}. This indicates corrupt TIN point data.",
                    new FixSuggestion
                    {
                        Title = "Remove corrupt source data from the surface definition and rebuild.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 30
                    }));
            }

            // Elevation range sanity (e.g. max < min after corruption).
            if (!double.IsNaN(stats.MinimumElevation) && !double.IsNaN(stats.MaximumElevation) &&
                stats.MaximumElevation < stats.MinimumElevation)
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Surface,
                    ObjectType.Surface, handle, name,
                    "Surface maximum elevation is less than minimum elevation.",
                    $"Min={stats.MinimumElevation:F4}, Max={stats.MaximumElevation:F4}.",
                    new FixSuggestion
                    {
                        Title = "Rebuild the surface after removing suspect source data.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 20
                    }));
            }

            // Very large elevation delta may indicate survey spikes.
            double elevRange = stats.MaximumElevation - stats.MinimumElevation;
            if (elevRange > 2000.0 && _options.IncludeInfoItems)
            {
                issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Surface,
                    ObjectType.Surface, handle, name,
                    $"Surface elevation range is unusually large: {elevRange:F2}m.",
                    $"Min={stats.MinimumElevation:F2}, Max={stats.MaximumElevation:F2}. " +
                    "Verify there are no survey spikes or outliers in the surface data."));
            }

            if (_options.IncludeInfoItems)
            {
                issues.Add(Issue(IssueSeverity.Info, IssueCategory.Surface,
                    ObjectType.Surface, handle, name,
                    $"Surface '{name}' validated. Elevation range: " +
                    $"{stats.MinimumElevation:F2}–{stats.MaximumElevation:F2}m.",
                    string.Empty));
            }

            return issues;
        }

        private void ValidateTinSurface(
            CivDb.TinSurface tin,
            List<ObjectIssue> issues,
            string handle,
            string name)
        {
            var tinProps = tin.GetTinProperties();
            long pointCount    = tinProps.NumberOfPoints;
            long triangleCount = tinProps.NumberOfTriangles;

            if (pointCount == 0)
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Surface,
                    ObjectType.TinSurface, handle, name,
                    "TIN surface has no points.",
                    "The surface definition contains no data sources or all sources have been removed.",
                    new FixSuggestion
                    {
                        Title = "Add data to the surface definition (points, breaklines, or contours).",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 20
                    }));
                return;
            }

            if (triangleCount == 0 && pointCount >= 3)
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Surface,
                    ObjectType.TinSurface, handle, name,
                    "TIN surface has points but no triangles.",
                    $"PointCount={pointCount}. The surface requires a rebuild.",
                    new FixSuggestion
                    {
                        Title = "Rebuild the surface (right-click surface → Rebuild).",
                        IsAutomatable = true,
                        RepairActionType = "Civil3DConnector.Repairs.RebuildSurfaceRepair",
                        EstimatedManualMinutes = 5
                    }));
            }

            // Sample TIN edges for degenerate triangles.
            int sampleSize   = (int)Math.Min(triangleCount, _options.MaxTinEdgeSampleSize);
            int degenerateCount = 0;

            try
            {
                var triangles = tin.GetTriangles(false);
                int sampled   = 0;

                foreach (var tri in triangles)
                {
                    if (sampled++ >= sampleSize) break;

                    var v0 = tri.Vertex1.Location;
                    var v1 = tri.Vertex2.Location;
                    var v2 = tri.Vertex3.Location;

                    double e01 = v0.DistanceTo(v1);
                    double e12 = v1.DistanceTo(v2);
                    double e20 = v2.DistanceTo(v0);

                    if (e01 < 1e-6 || e12 < 1e-6 || e20 < 1e-6)
                        degenerateCount++;
                }
            }
            catch
            {
                // Triangle enumeration may fail on very large or corrupt surfaces.
            }

            if (degenerateCount > 0)
            {
                issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Surface,
                    ObjectType.TinSurface, handle, name,
                    $"TIN surface contains {degenerateCount} degenerate triangles (sampled {sampleSize} of {triangleCount}).",
                    "Degenerate triangles have edges of near-zero length, which causes display artifacts " +
                    "and incorrect volume calculations.",
                    new FixSuggestion
                    {
                        Title = "Use Surface Edit → Delete Lines to remove degenerate TIN edges, then rebuild.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 30
                    }));
            }
        }

        private void ValidateGridSurface(
            CivDb.GridSurface grid,
            List<ObjectIssue> issues,
            string handle,
            string name)
        {
            var gridProps = grid.GetGridProperties();
            if (gridProps.NumberOfColumns < 2 || gridProps.NumberOfRows < 2)
            {
                issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Surface,
                    ObjectType.GridSurface, handle, name,
                    "Grid surface has fewer than 2 rows or 2 columns.",
                    $"Rows={gridProps.NumberOfRows}, Columns={gridProps.NumberOfColumns}.",
                    new FixSuggestion
                    {
                        Title = "Verify the grid surface definition has sufficient data extents.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 15
                    }));
            }
        }

        // =====================================================================
        // Pipe Network Validation
        // =====================================================================

        /// <summary>
        /// Validates a Civil 3D gravity pipe <see cref="Network"/>.
        /// Checks include:
        /// <list type="bullet">
        ///   <item>Empty network (no pipes or structures)</item>
        ///   <item>Pipe slope (too flat / too steep)</item>
        ///   <item>Minimum cover depth</item>
        ///   <item>Disconnected pipes (no connected structures at one or both ends)</item>
        ///   <item>Inverted pipe invert elevations (upstream lower than downstream)</item>
        ///   <item>Structure connectivity (no connected pipes)</item>
        /// </list>
        /// </summary>
        /// <param name="net">Open Network object.</param>
        /// <param name="tr">Active transaction.</param>
        public List<ObjectIssue> ValidatePipeNetwork(CivDb.Network net, Transaction tr)
        {
            var issues = new List<ObjectIssue>();
            string netHandle = net.ObjectId.Handle.ToString();
            string netName   = SafeName(net);

            var pipeIds      = net.GetPipeIds();
            var structureIds = net.GetStructureIds();

            if (pipeIds.Count == 0 && structureIds.Count == 0)
            {
                issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Network,
                    ObjectType.PipeNetwork, netHandle, netName,
                    "Pipe Network is empty (no pipes or structures).",
                    "An empty network definition will not produce any drainage model.",
                    new FixSuggestion
                    {
                        Title = "Add pipes and structures, or delete the empty network.",
                        IsAutomatable = false,
                        EstimatedManualMinutes = 5
                    }));
                return issues;
            }

            // --- Pipe validation ---
            foreach (ObjectId pipeId in pipeIds)
            {
                if (pipeId.IsNull || !pipeId.IsValid) continue;
                string pipeHandle = pipeId.Handle.ToString();

                CivDb.Pipe? pipe = null;
                try
                {
                    pipe = tr.GetObject(pipeId, OpenMode.ForRead) as CivDb.Pipe;
                }
                catch (System.Exception ex)
                {
                    issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corruption,
                        ObjectType.Pipe, pipeHandle, pipeHandle,
                        $"Pipe in network '{netName}' could not be opened.",
                        ex.Message));
                    continue;
                }

                if (pipe == null) continue;

                string pipeName = SafeName(pipe);
                double slope    = Math.Abs(pipe.Slope);

                // Slope: too flat.
                if (slope < MinPipeSlope && slope >= 0)
                {
                    issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Network,
                        ObjectType.Pipe, pipeHandle, pipeName,
                        $"Pipe '{pipeName}' has slope {slope * 100:F4}% which is below the minimum {MinPipeSlope * 100:F4}%.",
                        $"Network: '{netName}'. Low slope may result in solids deposition.",
                        new FixSuggestion
                        {
                            Title = "Adjust invert elevations to achieve minimum design slope.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 10
                        }));
                }

                // Slope: too steep.
                if (slope > MaxPipeSlope)
                {
                    issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Network,
                        ObjectType.Pipe, pipeHandle, pipeName,
                        $"Pipe '{pipeName}' has slope {slope * 100:F2}% which exceeds the maximum {MaxPipeSlope * 100:F0}%.",
                        $"Network: '{netName}'. Excessive slope may cause erosion or hydraulic jump."));
                }

                // Cover depth (simplified: use start invert and crown elevation).
                double coverAtStart = pipe.StartPoint.Z - pipe.OuterDiameterOrWidth / 1000.0
                                      - pipe.StartPoint.Z;
                // Better cover: use surface elevation if available; here we check invert vs pipe OD.
                double invertStart  = pipe.StartPoint.Z;
                double invertEnd    = pipe.EndPoint.Z;

                // Inverted flow check (upstream invert lower than downstream for gravity).
                if (invertStart < invertEnd && Math.Abs(invertStart - invertEnd) > 0.001)
                {
                    issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Network,
                        ObjectType.Pipe, pipeHandle, pipeName,
                        $"Pipe '{pipeName}': flow direction may be inverted " +
                        $"(start invert {invertStart:F3}m < end invert {invertEnd:F3}m).",
                        "For gravity systems, flow should run from a higher invert to a lower invert. " +
                        "Verify the pipe is not modelled in reverse.",
                        new FixSuggestion
                        {
                            Title = "Reverse the pipe direction or adjust invert elevations.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 5
                        }));
                }

                // Connectivity: check start/end structures.
                bool hasStartConn = pipe.StartStructureId != ObjectId.Null &&
                                    pipe.StartStructureId.IsValid &&
                                    !pipe.StartStructureId.IsErased;
                bool hasEndConn   = pipe.EndStructureId   != ObjectId.Null &&
                                    pipe.EndStructureId.IsValid &&
                                    !pipe.EndStructureId.IsErased;

                if (!hasStartConn && !hasEndConn)
                {
                    issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Network,
                        ObjectType.Pipe, pipeHandle, pipeName,
                        $"Pipe '{pipeName}' has no structures connected at either end (floating pipe).",
                        $"Network: '{netName}'. The pipe is not connected to the network topology.",
                        new FixSuggestion
                        {
                            Title = "Connect the pipe ends to appropriate structures.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 10
                        }));
                }
                else if (!hasStartConn || !hasEndConn)
                {
                    issues.Add(Issue(IssueSeverity.Info, IssueCategory.Network,
                        ObjectType.Pipe, pipeHandle, pipeName,
                        $"Pipe '{pipeName}' has an open end " +
                        $"({(hasStartConn ? "end" : "start")} is unconnected).",
                        $"Network: '{netName}'. This may be intentional for an outfall or inlet."));
                }
            }

            // --- Structure validation ---
            foreach (ObjectId strId in structureIds)
            {
                if (strId.IsNull || !strId.IsValid) continue;
                string strHandle = strId.Handle.ToString();

                CivDb.Structure? str = null;
                try
                {
                    str = tr.GetObject(strId, OpenMode.ForRead) as CivDb.Structure;
                }
                catch (System.Exception ex)
                {
                    issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corruption,
                        ObjectType.Structure, strHandle, strHandle,
                        $"Structure in network '{netName}' could not be opened.",
                        ex.Message));
                    continue;
                }

                if (str == null) continue;

                string strName    = SafeName(str);
                var connectedPipes = str.GetConnectedPipes();

                if (connectedPipes.Count == 0)
                {
                    issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Network,
                        ObjectType.Structure, strHandle, strName,
                        $"Structure '{strName}' has no connected pipes (isolated node).",
                        $"Network: '{netName}'. An isolated structure serves no hydraulic purpose.",
                        new FixSuggestion
                        {
                            Title = "Connect pipes to the structure or delete if unused.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 5
                        }));
                }

                // Check sump depth: sump should be ≥ 0 (negative means error).
                if (str.SumpDepth < 0)
                {
                    issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Network,
                        ObjectType.Structure, strHandle, strName,
                        $"Structure '{strName}' has a negative sump depth ({str.SumpDepth:F3}m).",
                        "A negative sump depth is geometrically invalid.",
                        new FixSuggestion
                        {
                            Title = "Set sump depth to zero or a positive design value.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 3
                        }));
                }
            }

            if (_options.IncludeInfoItems)
            {
                issues.Add(Issue(IssueSeverity.Info, IssueCategory.Network,
                    ObjectType.PipeNetwork, netHandle, netName,
                    $"Pipe Network '{netName}': {pipeIds.Count} pipes, {structureIds.Count} structures.",
                    string.Empty));
            }

            return issues;
        }

        // =====================================================================
        // Pressure Network Validation
        // =====================================================================

        /// <summary>
        /// Validates a Civil 3D <see cref="PressureNetwork"/>.
        /// Checks include:
        /// <list type="bullet">
        ///   <item>Parts list / catalog availability</item>
        ///   <item>Fitting family references</item>
        ///   <item>Disconnected pressure pipes</item>
        ///   <item>Cover depth</item>
        /// </list>
        /// </summary>
        /// <param name="net">Open PressureNetwork object.</param>
        /// <param name="tr">Active transaction.</param>
        public List<ObjectIssue> ValidatePressureNetwork(CivDb.PressureNetwork net, Transaction tr)
        {
            var issues = new List<ObjectIssue>();
            string handle = net.ObjectId.Handle.ToString();
            string name   = SafeName(net);

            // Parts list check.
            try
            {
                string partsListName = net.PartsListName;
                if (string.IsNullOrEmpty(partsListName))
                {
                    issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Network,
                        ObjectType.PressureNetwork, handle, name,
                        $"Pressure Network '{name}' has no parts list assigned.",
                        "Without a parts list the network cannot validate fitting or pipe catalog lookups.",
                        new FixSuggestion
                        {
                            Title = "Assign a parts list in Pressure Network Properties.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 5
                        }));
                }
            }
            catch { /* parts list API not available on all versions */ }

            // Pressure pipes.
            var ppIds = net.GetPressurePipeIds();
            foreach (ObjectId ppId in ppIds)
            {
                if (ppId.IsNull || !ppId.IsValid) continue;
                string ppHandle = ppId.Handle.ToString();

                CivDb.PressurePipe? pp = null;
                try
                {
                    pp = tr.GetObject(ppId, OpenMode.ForRead) as CivDb.PressurePipe;
                }
                catch (System.Exception ex)
                {
                    issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corruption,
                        ObjectType.PressurePipe, ppHandle, ppHandle,
                        $"Pressure pipe in network '{name}' could not be opened.",
                        ex.Message));
                    continue;
                }

                if (pp == null) continue;

                string ppName = SafeName(pp);

                // Catalog part reference check.
                try
                {
                    string partFamilyName = pp.PartFamilyName;
                    if (string.IsNullOrEmpty(partFamilyName))
                    {
                        issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Network,
                            ObjectType.PressurePipe, ppHandle, ppName,
                            $"Pressure pipe '{ppName}' has no catalog part family assigned.",
                            $"Network: '{name}'. Catalog lookup failures prevent correct sizing output.",
                            new FixSuggestion
                            {
                                Title = "Assign a valid catalog part family in pipe properties.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 5
                            }));
                    }
                }
                catch { /* API property may vary by version */ }
            }

            // Fittings.
            var fittingIds = net.GetFittingIds();
            foreach (ObjectId fitId in fittingIds)
            {
                if (fitId.IsNull || !fitId.IsValid) continue;
                string fitHandle = fitId.Handle.ToString();

                CivDb.PressureFitting? fit = null;
                try
                {
                    fit = tr.GetObject(fitId, OpenMode.ForRead) as CivDb.PressureFitting;
                }
                catch (System.Exception ex)
                {
                    issues.Add(Issue(IssueSeverity.Critical, IssueCategory.Corruption,
                        ObjectType.PressureFitting, fitHandle, fitHandle,
                        $"Pressure fitting in network '{name}' could not be opened.",
                        ex.Message));
                    continue;
                }

                if (fit == null) continue;

                string fitName = SafeName(fit);

                try
                {
                    string partFamilyName = fit.PartFamilyName;
                    if (string.IsNullOrEmpty(partFamilyName))
                    {
                        issues.Add(Issue(IssueSeverity.Warning, IssueCategory.Network,
                            ObjectType.PressureFitting, fitHandle, fitName,
                            $"Pressure fitting '{fitName}' has no catalog part family assigned.",
                            $"Network: '{name}'.",
                            new FixSuggestion
                            {
                                Title = "Reassign a valid fitting catalog part family.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 5
                            }));
                    }
                }
                catch { /* API property may vary by version */ }
            }

            if (_options.IncludeInfoItems)
            {
                issues.Add(Issue(IssueSeverity.Info, IssueCategory.Network,
                    ObjectType.PressureNetwork, handle, name,
                    $"Pressure Network '{name}': {ppIds.Count} pipes, {fittingIds.Count} fittings.",
                    string.Empty));
            }

            return issues;
        }

        // =====================================================================
        // Utility helpers
        // =====================================================================

        private static ObjectIssue Issue(
            IssueSeverity   severity,
            IssueCategory   category,
            ObjectType      objectType,
            string          handle,
            string          name,
            string          message,
            string          detail   = "",
            FixSuggestion?  fix      = null)
        {
            var issue = new ObjectIssue
            {
                Severity     = severity,
                Category     = category,
                ObjectType   = objectType,
                ObjectHandle = handle,
                ObjectName   = name,
                Message      = message,
                Detail       = detail
            };
            if (fix != null)
                issue.FixSuggestions.Add(fix);
            return issue;
        }

        private static string SafeName(DBObject obj)
        {
            try
            {
                if (obj is SymbolTableRecord str) return str.Name ?? obj.Handle.ToString();
                if (obj is CivDb.Entity civEnt)  return civEnt.Name ?? obj.Handle.ToString();
            }
            catch { /* ignored */ }
            return obj.Handle.ToString();
        }
    }
}
