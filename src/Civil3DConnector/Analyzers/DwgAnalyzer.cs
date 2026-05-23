// ============================================================================
// DwgAnalyzer.cs
// Civil 3D DWG Analysis – Deep Scanner
// Compatible with Autodesk Civil 3D 2025 / 2026 / 2027
// ============================================================================
//
// AutoCAD / Civil 3D assembly references required in the .csproj:
//   acdbmgd.dll           (Autodesk.AutoCAD.DatabaseServices)
//   acmgd.dll             (Autodesk.AutoCAD.ApplicationServices)
//   AeccDbMgd.dll         (Autodesk.Civil.DatabaseServices)
//   AeccLandDbMgd.dll     (Autodesk.Civil.Land.DatabaseServices – legacy surfaces)
//   AeccPipesMgd.dll      (Autodesk.Civil.DatabaseServices pipe/pressure)
//
// All referenced assemblies must be set CopyLocal = false and placed on the
// AutoCAD install path so they are loaded by the host application at runtime.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Civil3DConnector.Models;

using AcDb  = Autodesk.AutoCAD.DatabaseServices;
using CivDb = Autodesk.Civil.DatabaseServices;

namespace Civil3DConnector.Analyzers
{
    /// <summary>
    /// Options that control the depth and scope of a DWG analysis run.
    /// </summary>
    public sealed class AnalyzerOptions
    {
        /// <summary>
        /// Scan Alignments. Default: <c>true</c>.
        /// </summary>
        public bool ScanAlignments { get; set; } = true;

        /// <summary>Scan Profiles and Profile Views. Default: <c>true</c>.</summary>
        public bool ScanProfiles { get; set; } = true;

        /// <summary>Scan Corridors and Assemblies. Default: <c>true</c>.</summary>
        public bool ScanCorridors { get; set; } = true;

        /// <summary>Scan Surfaces (TIN and Grid). Default: <c>true</c>.</summary>
        public bool ScanSurfaces { get; set; } = true;

        /// <summary>Scan Pipe Networks. Default: <c>true</c>.</summary>
        public bool ScanPipeNetworks { get; set; } = true;

        /// <summary>Scan Pressure Networks. Default: <c>true</c>.</summary>
        public bool ScanPressureNetworks { get; set; } = true;

        /// <summary>Scan Feature Lines. Default: <c>true</c>.</summary>
        public bool ScanFeatureLines { get; set; } = true;

        /// <summary>
        /// Validate Data Shortcuts (requires the project folder to be accessible).
        /// Default: <c>true</c>.
        /// </summary>
        public bool ScanDataShortcuts { get; set; } = true;

        /// <summary>Validate XREFs attached to the drawing. Default: <c>true</c>.</summary>
        public bool ScanXRefs { get; set; } = true;

        /// <summary>Check label styles referenced by Civil 3D objects. Default: <c>true</c>.</summary>
        public bool ScanLabelStyles { get; set; } = true;

        /// <summary>
        /// Check for coordinate system / projection mismatches. Default: <c>true</c>.
        /// </summary>
        public bool ScanCoordinateSystem { get; set; } = true;

        /// <summary>
        /// Maximum number of TIN edges to sample per surface when verifying geometry.
        /// A lower value speeds up analysis on very large surfaces. Default: 50 000.
        /// </summary>
        public int MaxTinEdgeSampleSize { get; set; } = 50_000;

        /// <summary>
        /// When <c>true</c> the analyser records Info-level items in addition to
        /// Warnings and Critical issues. Default: <c>true</c>.
        /// </summary>
        public bool IncludeInfoItems { get; set; } = true;

        /// <summary>Default options (all scans enabled).</summary>
        public static AnalyzerOptions Default => new AnalyzerOptions();

        /// <summary>Fastest option set – only critical structural scans.</summary>
        public static AnalyzerOptions Quick => new AnalyzerOptions
        {
            ScanLabelStyles      = false,
            ScanDataShortcuts    = false,
            ScanFeatureLines     = false,
            MaxTinEdgeSampleSize = 5_000,
            IncludeInfoItems     = false
        };
    }

    /// <summary>
    /// Progress notification raised during a long-running analysis.
    /// </summary>
    public sealed class AnalysisProgress
    {
        /// <summary>0–100 percentage complete.</summary>
        public int PercentComplete { get; init; }

        /// <summary>Human-readable status message.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>Phase currently being executed.</summary>
        public string Phase { get; init; } = string.Empty;
    }

    // =========================================================================
    // DwgAnalyzer – main entry point
    // =========================================================================

    /// <summary>
    /// High-performance DWG scanner that inspects an Autodesk Civil 3D drawing
    /// for corrupted objects, invalid references, style problems, and geometry
    /// errors.
    ///
    /// <para>
    /// The analyser is designed to run inside the AutoCAD/Civil 3D host process
    /// where the managed database is available.  A background-thread (non-UI)
    /// execution model is supported via <c>AnalyzeAsync</c>.
    /// </para>
    ///
    /// <para>
    /// Call <see cref="AnalyzeAsync"/> with a loaded <see cref="Database"/> or a
    /// path to a DWG file. The method returns a fully-populated
    /// <see cref="AnalysisResult"/> that can be passed to
    /// <see cref="ReportGenerator"/> for HTML / JSON / CSV / XML output.
    /// </para>
    /// </summary>
    public sealed class DwgAnalyzer
    {
        // ------------------------------------------------------------------
        // Private state
        // ------------------------------------------------------------------

        private readonly AnalyzerOptions _options;
        private readonly IProgress<AnalysisProgress>? _progress;
        private AnalysisResult _result = null!;
        private Database _db = null!;
        private CivilDocument _civDoc = null!;

        // Phase weights used to calculate overall percent-complete.
        private static readonly (string Phase, int Weight)[] PhaseWeights =
        {
            ("Drawing Metadata",       2),
            ("XRefs",                  5),
            ("Coordinate System",      3),
            ("Alignments",            10),
            ("Profiles",              10),
            ("Corridors",             10),
            ("Surfaces",              15),
            ("Pipe Networks",         10),
            ("Pressure Networks",      8),
            ("Feature Lines",          7),
            ("Data Shortcuts",         5),
            ("Label Styles",           5),
            ("Object References",     10)
        };

        private int _totalWeight;
        private int _completedWeight;

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Initialises the analyser with the specified options and optional
        /// progress-reporting callback.
        /// </summary>
        /// <param name="options">
        /// Scanner options; pass <c>null</c> for <see cref="AnalyzerOptions.Default"/>.
        /// </param>
        /// <param name="progress">
        /// Optional <see cref="IProgress{T}"/> for progress notifications.
        /// </param>
        public DwgAnalyzer(AnalyzerOptions? options = null,
                           IProgress<AnalysisProgress>? progress = null)
        {
            _options  = options ?? AnalyzerOptions.Default;
            _progress = progress;
            _totalWeight = PhaseWeights.Sum(p => p.Weight);
        }

        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        /// <summary>
        /// Analyses the drawing currently associated with the provided
        /// <see cref="Database"/> (which must be open and valid in the host process).
        /// </summary>
        /// <param name="db">Open AutoCAD database to scan.</param>
        /// <param name="cancellationToken">Optional cancellation token.</param>
        /// <returns>Populated <see cref="AnalysisResult"/>.</returns>
        public async Task<AnalysisResult> AnalyzeAsync(
            Database db,
            CancellationToken cancellationToken = default)
        {
            if (db is null) throw new ArgumentNullException(nameof(db));

            _db     = db;
            _result = new AnalysisResult { StartedAt = DateTime.UtcNow };

            try
            {
                _civDoc = CivilApplication.ActiveDocument;
            }
            catch (System.Exception ex)
            {
                // Civil 3D API not available – flag but continue with basic scan.
                _result.AddIssue(MakeIssue(
                    IssueSeverity.Warning,
                    IssueCategory.General,
                    ObjectType.Unknown,
                    handle: string.Empty,
                    name: "CivilDocument",
                    message: "Civil 3D document context unavailable – Civil-specific checks skipped.",
                    detail: ex.Message));
            }

            await Task.Run(() => RunAllPhases(cancellationToken), cancellationToken)
                      .ConfigureAwait(false);

            _result.CompletedAt = DateTime.UtcNow;
            _result.ComputeSummary();
            return _result;
        }

        // ------------------------------------------------------------------
        // Phase orchestration
        // ------------------------------------------------------------------

        private void RunAllPhases(CancellationToken ct)
        {
            _completedWeight = 0;

            try
            {
                RunPhase("Drawing Metadata",    2,  () => ScanDrawingMetadata());
                ct.ThrowIfCancellationRequested();

                if (_options.ScanXRefs)
                {
                    RunPhase("XRefs",           5,  () => ScanXRefs());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanCoordinateSystem)
                {
                    RunPhase("Coordinate System", 3, () => ScanCoordinateSystem());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanAlignments)
                {
                    RunPhase("Alignments",      10, () => ScanAlignments());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanProfiles)
                {
                    RunPhase("Profiles",        10, () => ScanProfiles());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanCorridors)
                {
                    RunPhase("Corridors",       10, () => ScanCorridors());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanSurfaces)
                {
                    RunPhase("Surfaces",        15, () => ScanSurfaces());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanPipeNetworks)
                {
                    RunPhase("Pipe Networks",   10, () => ScanPipeNetworks());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanPressureNetworks)
                {
                    RunPhase("Pressure Networks", 8, () => ScanPressureNetworks());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanFeatureLines)
                {
                    RunPhase("Feature Lines",    7, () => ScanFeatureLines());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanDataShortcuts)
                {
                    RunPhase("Data Shortcuts",   5, () => ScanDataShortcuts());
                    ct.ThrowIfCancellationRequested();
                }

                if (_options.ScanLabelStyles)
                {
                    RunPhase("Label Styles",     5, () => ScanLabelStyles());
                    ct.ThrowIfCancellationRequested();
                }

                RunPhase("Object References",  10, () => ScanObjectReferences());
            }
            catch (OperationCanceledException)
            {
                _result.IsSuccessful = false;
                _result.FatalError   = "Analysis cancelled by the caller.";
            }
            catch (System.Exception ex)
            {
                _result.IsSuccessful = false;
                _result.FatalError   = $"Unhandled exception: {ex.GetType().Name}: {ex.Message}";
            }
        }

        private void RunPhase(string name, int weight, Action action)
        {
            Report(name, (int)((_completedWeight * 100.0) / _totalWeight));
            try
            {
                action();
            }
            catch (System.Exception ex)
            {
                _result.AddIssue(MakeIssue(
                    IssueSeverity.Critical,
                    IssueCategory.Corruption,
                    ObjectType.Unknown,
                    handle: string.Empty,
                    name: name,
                    message: $"Phase '{name}' failed with an unhandled exception.",
                    detail: ex.ToString()));
            }
            _completedWeight += weight;
        }

        private void Report(string phase, int pct)
        {
            _progress?.Report(new AnalysisProgress
            {
                Phase           = phase,
                PercentComplete = Math.Min(pct, 99),
                Message         = $"Scanning {phase}…"
            });
        }

        // =====================================================================
        // Phase 1 – Drawing Metadata
        // =====================================================================

        private void ScanDrawingMetadata()
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                _result.DwgFilePath    = _db.Filename ?? string.Empty;
                _result.DrawingVersion = _db.OriginalFileVersion.ToString();

                if (!string.IsNullOrEmpty(_result.DwgFilePath) &&
                    File.Exists(_result.DwgFilePath))
                {
                    _result.FileSizeBytes = new FileInfo(_result.DwgFilePath).Length;
                }

                // Extract Civil 3D version from drawing custom properties.
                var summInfo = (DatabaseSummaryInfo)tr.GetObject(_db.SummaryInfoId, OpenMode.ForRead);
                // Custom properties are in summInfo.CustomProperties – walk via iterator.
                var propIter = summInfo.CustomProperties;
                while (propIter.MoveNext())
                {
                    var entry = (DictionaryEntry)propIter.Current;
                    if (string.Equals(entry.Key?.ToString(), "Civil3DVersion",
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        _result.Civil3DVersion = entry.Value?.ToString() ?? string.Empty;
                    }
                }

                // Coordinate system from Civil document if available.
                if (_civDoc != null)
                {
                    try
                    {
                        _result.CoordinateSystem =
                            _civDoc.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode
                            ?? string.Empty;
                    }
                    catch { /* may fail for older Civil API versions */ }
                }

                // Check minimum drawing version (AC1032 = AutoCAD 2018 = minimum for C3D 2025).
                if (string.Compare(_result.DrawingVersion, "AC1032",
                                   StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _result.AddIssue(MakeIssue(
                        IssueSeverity.Warning,
                        IssueCategory.General,
                        ObjectType.Unknown,
                        handle: string.Empty,
                        name: "Drawing",
                        message: $"Drawing version '{_result.DrawingVersion}' is older than AC1032 (AutoCAD 2018).",
                        detail: "Upgrading to a newer drawing format may be required for Civil 3D 2025+ features.",
                        fix: new FixSuggestion
                        {
                            Title = "Save the drawing to the latest AutoCAD format.",
                            Description = "Use SAVEAS and select the current version DWG format.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 2
                        }));
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Phase 2 – XRefs
        // =====================================================================

        private void ScanXRefs()
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (!btr.IsFromExternalReference && !btr.IsFromOverlayReference)
                        continue;

                    string xrefName = btr.Name;
                    string xrefPath = btr.PathName;
                    string handle   = btrId.Handle.ToString();

                    var record = new ObjectRecord
                    {
                        Handle = handle,
                        Name   = xrefName,
                        Type   = ObjectType.XRef,
                        Layer  = "0"
                    };
                    _result.ObjectInventory.Add(record);

                    // Resolve the XREF path.
                    string resolvedPath = ResolveXRefPath(xrefPath, _result.DwgFilePath);

                    if (!File.Exists(resolvedPath))
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Critical,
                            IssueCategory.Reference,
                            ObjectType.XRef,
                            handle: handle,
                            name: xrefName,
                            message: $"XREF '{xrefName}' cannot be found at path '{xrefPath}'.",
                            detail: $"Resolved path attempted: '{resolvedPath}'. " +
                                    "Objects that depend on this XREF may display incorrectly or cause errors.",
                            fix: new FixSuggestion
                            {
                                Title = "Repath the XREF using the XREF Manager.",
                                Description = "In the XREF Manager (XM command), right-click the XREF and " +
                                              "choose 'Select New Path' to point to the correct file location.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 5
                            }));
                    }
                    else if (btr.XrefStatus == XrefStatus.Unloaded)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Warning,
                            IssueCategory.Reference,
                            ObjectType.XRef,
                            handle: handle,
                            name: xrefName,
                            message: $"XREF '{xrefName}' is unloaded.",
                            detail: "The XREF file exists but has been manually unloaded. " +
                                    "Civil 3D label styles that reference objects inside the XREF may appear blank."));
                    }
                    else if (btr.XrefStatus == XrefStatus.FileNotFound)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Critical,
                            IssueCategory.Reference,
                            ObjectType.XRef,
                            handle: handle,
                            name: xrefName,
                            message: $"XREF '{xrefName}': AutoCAD reports FileNotFound status.",
                            detail: $"Stored path: '{xrefPath}'.",
                            fix: new FixSuggestion
                            {
                                Title = "Locate and repath the missing XREF file.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 10
                            }));
                    }
                    else if (btr.XrefStatus == XrefStatus.Resolved &&
                             _options.IncludeInfoItems)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Info,
                            IssueCategory.Reference,
                            ObjectType.XRef,
                            handle: handle,
                            name: xrefName,
                            message: $"XREF '{xrefName}' is loaded and resolved.",
                            detail: $"Path: '{xrefPath}'."));
                    }
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        private static string ResolveXRefPath(string storedPath, string hostDwgPath)
        {
            if (Path.IsPathRooted(storedPath))
                return storedPath;

            string? hostDir = Path.GetDirectoryName(hostDwgPath);
            if (hostDir != null)
            {
                string candidate = Path.GetFullPath(Path.Combine(hostDir, storedPath));
                if (File.Exists(candidate))
                    return candidate;
            }
            return storedPath;
        }

        // =====================================================================
        // Phase 3 – Coordinate System
        // =====================================================================

        private void ScanCoordinateSystem()
        {
            if (_civDoc == null) return;

            try
            {
                string csCode = _civDoc.Settings.DrawingSettings.UnitZoneSettings
                                       .CoordinateSystemCode ?? string.Empty;

                if (string.IsNullOrWhiteSpace(csCode))
                {
                    _result.AddIssue(MakeIssue(
                        IssueSeverity.Warning,
                        IssueCategory.CoordinateSystem,
                        ObjectType.Unknown,
                        handle: string.Empty,
                        name: "Drawing Settings",
                        message: "No coordinate system is assigned to this drawing.",
                        detail: "Without a coordinate system, geolocation features, " +
                                "XRef projection transformations, and Data Shortcut " +
                                "alignment will not function correctly.",
                        fix: new FixSuggestion
                        {
                            Title = "Assign a coordinate system in Drawing Settings.",
                            Description = "In Civil 3D, go to Settings tab → right-click drawing name → " +
                                          "Edit Drawing Settings → Units and Zone tab → select the correct " +
                                          "coordinate system.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 3
                        }));
                    return;
                }

                // Validate the CS code is recognized by the Civil 3D API.
                try
                {
                    var csInfo = CivilApplication.GetCoordSystemInfo(csCode);
                    if (csInfo == null)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Critical,
                            IssueCategory.CoordinateSystem,
                            ObjectType.Unknown,
                            handle: string.Empty,
                            name: "Drawing Settings",
                            message: $"Coordinate system code '{csCode}' is not recognized by Civil 3D.",
                            detail: "The stored coordinate system code does not match any entry " +
                                    "in the Civil 3D coordinate system library. Survey transformations " +
                                    "and surface projections will fail.",
                            fix: new FixSuggestion
                            {
                                Title = "Correct the coordinate system code.",
                                Description = "Open Drawing Settings, navigate to Units and Zone, " +
                                              "and select a valid coordinate system from the dropdown.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 5
                            }));
                    }
                    else if (_options.IncludeInfoItems)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Info,
                            IssueCategory.CoordinateSystem,
                            ObjectType.Unknown,
                            handle: string.Empty,
                            name: "Drawing Settings",
                            message: $"Coordinate system '{csCode}' is valid.",
                            detail: $"Description: {csInfo.Description}"));
                    }
                }
                catch (System.Exception ex)
                {
                    _result.AddIssue(MakeIssue(
                        IssueSeverity.Warning,
                        IssueCategory.CoordinateSystem,
                        ObjectType.Unknown,
                        handle: string.Empty,
                        name: "Drawing Settings",
                        message: $"Coordinate system validation threw an exception: {ex.Message}",
                        detail: ex.ToString()));
                }
            }
            catch (System.Exception ex)
            {
                _result.AddIssue(MakeIssue(
                    IssueSeverity.Warning,
                    IssueCategory.CoordinateSystem,
                    ObjectType.Unknown,
                    handle: string.Empty,
                    name: "Drawing Settings",
                    message: "Could not read coordinate system settings.",
                    detail: ex.Message));
            }
        }

        // =====================================================================
        // Phase 4 – Alignments
        // =====================================================================

        private void ScanAlignments()
        {
            if (_civDoc == null) return;

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var alignIds = _civDoc.GetAlignmentIds();
                foreach (ObjectId id in alignIds)
                {
                    if (id.IsNull || !id.IsValid) continue;

                    CivDb.Alignment? al = null;
                    string handle = id.Handle.ToString();

                    try
                    {
                        al = tr.GetObject(id, OpenMode.ForRead) as CivDb.Alignment;
                    }
                    catch (System.Exception ex)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Critical,
                            IssueCategory.Corruption,
                            ObjectType.Alignment,
                            handle: handle,
                            name: handle,
                            message: "Alignment object could not be opened – possible database corruption.",
                            detail: ex.Message));
                        continue;
                    }

                    if (al == null) continue;

                    string name = SafeName(al);
                    var record = new ObjectRecord
                    {
                        Handle = handle,
                        Name   = name,
                        Type   = ObjectType.Alignment,
                        Layer  = al.Layer ?? string.Empty
                    };
                    record.Properties["Length"] = al.Length.ToString("F3");
                    record.Properties["StartStation"] = al.StartingStation.ToString("F3");
                    record.Properties["EndStation"] = al.EndingStation.ToString("F3");
                    _result.ObjectInventory.Add(record);

                    // Delegate deep validation to ObjectValidator.
                    var validator = new ObjectValidator(_options, _db, _civDoc);
                    var issues    = validator.ValidateAlignment(al, tr);
                    foreach (var issue in issues)
                        _result.AddIssue(issue);
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Phase 5 – Profiles
        // =====================================================================

        private void ScanProfiles()
        {
            if (_civDoc == null) return;

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                // Profiles are accessed via each Alignment.
                var alignIds = _civDoc.GetAlignmentIds();
                foreach (ObjectId alId in alignIds)
                {
                    if (alId.IsNull || !alId.IsValid) continue;
                    CivDb.Alignment? al = null;
                    try
                    {
                        al = tr.GetObject(alId, OpenMode.ForRead) as CivDb.Alignment;
                    }
                    catch { continue; }
                    if (al == null) continue;

                    foreach (ObjectId profId in al.GetProfileIds())
                    {
                        if (profId.IsNull || !profId.IsValid) continue;
                        string handle = profId.Handle.ToString();

                        CivDb.Profile? prof = null;
                        try
                        {
                            prof = tr.GetObject(profId, OpenMode.ForRead) as CivDb.Profile;
                        }
                        catch (System.Exception ex)
                        {
                            _result.AddIssue(MakeIssue(
                                IssueSeverity.Critical,
                                IssueCategory.Corruption,
                                ObjectType.Profile,
                                handle: handle,
                                name: handle,
                                message: "Profile object could not be opened – possible database corruption.",
                                detail: ex.Message));
                            continue;
                        }

                        if (prof == null) continue;

                        string name = SafeName(prof);
                        var record = new ObjectRecord
                        {
                            Handle = handle,
                            Name   = name,
                            Type   = ObjectType.Profile,
                            Layer  = prof.Layer ?? string.Empty
                        };
                        _result.ObjectInventory.Add(record);

                        var validator = new ObjectValidator(_options, _db, _civDoc);
                        var issues    = validator.ValidateProfile(prof, al, tr);
                        foreach (var issue in issues)
                            _result.AddIssue(issue);
                    }
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Phase 6 – Corridors
        // =====================================================================

        private void ScanCorridors()
        {
            if (_civDoc == null) return;

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var corrIds = _civDoc.GetCorridorIds();
                foreach (ObjectId id in corrIds)
                {
                    if (id.IsNull || !id.IsValid) continue;
                    string handle = id.Handle.ToString();

                    CivDb.Corridor? corr = null;
                    try
                    {
                        corr = tr.GetObject(id, OpenMode.ForRead) as CivDb.Corridor;
                    }
                    catch (System.Exception ex)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Critical,
                            IssueCategory.Corruption,
                            ObjectType.Corridor,
                            handle: handle,
                            name: handle,
                            message: "Corridor object could not be opened.",
                            detail: ex.Message));
                        continue;
                    }

                    if (corr == null) continue;

                    string name = SafeName(corr);
                    var record = new ObjectRecord
                    {
                        Handle = handle,
                        Name   = name,
                        Type   = ObjectType.Corridor,
                        Layer  = corr.Layer ?? string.Empty
                    };
                    _result.ObjectInventory.Add(record);

                    var validator = new ObjectValidator(_options, _db, _civDoc);
                    var issues    = validator.ValidateCorridor(corr, tr);
                    foreach (var issue in issues)
                        _result.AddIssue(issue);
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Phase 7 – Surfaces
        // =====================================================================

        private void ScanSurfaces()
        {
            if (_civDoc == null) return;

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var surfIds = _civDoc.GetSurfaceIds();
                foreach (ObjectId id in surfIds)
                {
                    if (id.IsNull || !id.IsValid) continue;
                    string handle = id.Handle.ToString();

                    CivDb.Surface? surf = null;
                    try
                    {
                        surf = tr.GetObject(id, OpenMode.ForRead) as CivDb.Surface;
                    }
                    catch (System.Exception ex)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Critical,
                            IssueCategory.Corruption,
                            ObjectType.Surface,
                            handle: handle,
                            name: handle,
                            message: "Surface object could not be opened.",
                            detail: ex.Message));
                        continue;
                    }

                    if (surf == null) continue;

                    string name = SafeName(surf);
                    ObjectType surfType = surf is CivDb.TinSurface ? ObjectType.TinSurface :
                                         surf is CivDb.GridSurface  ? ObjectType.GridSurface :
                                         ObjectType.Surface;

                    var record = new ObjectRecord
                    {
                        Handle = handle,
                        Name   = name,
                        Type   = surfType,
                        Layer  = surf.Layer ?? string.Empty
                    };
                    _result.ObjectInventory.Add(record);

                    var validator = new ObjectValidator(_options, _db, _civDoc);
                    var issues    = validator.ValidateSurface(surf, tr);
                    foreach (var issue in issues)
                        _result.AddIssue(issue);
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Phase 8 – Pipe Networks
        // =====================================================================

        private void ScanPipeNetworks()
        {
            if (_civDoc == null) return;

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var netIds = _civDoc.GetPipeNetworkIds();
                foreach (ObjectId id in netIds)
                {
                    if (id.IsNull || !id.IsValid) continue;
                    string handle = id.Handle.ToString();

                    CivDb.Network? net = null;
                    try
                    {
                        net = tr.GetObject(id, OpenMode.ForRead) as CivDb.Network;
                    }
                    catch (System.Exception ex)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Critical,
                            IssueCategory.Corruption,
                            ObjectType.PipeNetwork,
                            handle: handle,
                            name: handle,
                            message: "Pipe Network object could not be opened.",
                            detail: ex.Message));
                        continue;
                    }

                    if (net == null) continue;

                    string name = SafeName(net);
                    var record = new ObjectRecord
                    {
                        Handle = handle,
                        Name   = name,
                        Type   = ObjectType.PipeNetwork,
                        Layer  = net.Layer ?? string.Empty
                    };
                    record.Properties["PipeCount"]      = net.GetPipeIds().Count.ToString();
                    record.Properties["StructureCount"] = net.GetStructureIds().Count.ToString();
                    _result.ObjectInventory.Add(record);

                    var validator = new ObjectValidator(_options, _db, _civDoc);
                    var issues    = validator.ValidatePipeNetwork(net, tr);
                    foreach (var issue in issues)
                        _result.AddIssue(issue);
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Phase 9 – Pressure Networks
        // =====================================================================

        private void ScanPressureNetworks()
        {
            if (_civDoc == null) return;

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var netIds = _civDoc.GetPressureNetworkIds();
                foreach (ObjectId id in netIds)
                {
                    if (id.IsNull || !id.IsValid) continue;
                    string handle = id.Handle.ToString();

                    CivDb.PressureNetwork? net = null;
                    try
                    {
                        net = tr.GetObject(id, OpenMode.ForRead) as CivDb.PressureNetwork;
                    }
                    catch (System.Exception ex)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Critical,
                            IssueCategory.Corruption,
                            ObjectType.PressureNetwork,
                            handle: handle,
                            name: handle,
                            message: "Pressure Network object could not be opened.",
                            detail: ex.Message));
                        continue;
                    }

                    if (net == null) continue;

                    string name = SafeName(net);
                    var record = new ObjectRecord
                    {
                        Handle = handle,
                        Name   = name,
                        Type   = ObjectType.PressureNetwork,
                        Layer  = net.Layer ?? string.Empty
                    };
                    _result.ObjectInventory.Add(record);

                    var validator = new ObjectValidator(_options, _db, _civDoc);
                    var issues    = validator.ValidatePressureNetwork(net, tr);
                    foreach (var issue in issues)
                        _result.AddIssue(issue);
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Phase 10 – Feature Lines
        // =====================================================================

        private void ScanFeatureLines()
        {
            if (_civDoc == null) return;

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(_db);
                var modelSpace   = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForRead);

                int featureLineCount = 0;
                int orphanCount      = 0;

                foreach (ObjectId entityId in modelSpace)
                {
                    if (entityId.IsNull || !entityId.IsValid) continue;

                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                    }
                    catch { continue; }

                    if (ent is not CivDb.FeatureLine fl) continue;

                    featureLineCount++;
                    string handle = entityId.Handle.ToString();
                    string name   = SafeName(fl);

                    var record = new ObjectRecord
                    {
                        Handle = handle,
                        Name   = name,
                        Type   = ObjectType.FeatureLine,
                        Layer  = fl.Layer ?? string.Empty
                    };
                    record.Properties["ElevationCount"] = fl.ElevationCount.ToString();
                    _result.ObjectInventory.Add(record);

                    // Check for feature lines not connected to any grading or site.
                    bool siteAssigned = fl.SiteId != ObjectId.Null && fl.SiteId.IsValid;
                    if (!siteAssigned)
                    {
                        orphanCount++;
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Warning,
                            IssueCategory.FeatureLine,
                            ObjectType.FeatureLine,
                            handle: handle,
                            name: name,
                            message: $"Feature Line '{name}' is not assigned to a site (orphaned).",
                            detail: "Orphaned feature lines cannot participate in grading operations " +
                                    "and may represent leftover geometry.",
                            fix: new FixSuggestion
                            {
                                Title = "Assign the feature line to a site or delete if unused.",
                                Description = "Right-click the feature line → Feature Line Properties → " +
                                              "assign to the appropriate site.",
                                IsAutomatable = false,
                                EstimatedManualMinutes = 3
                            }));
                    }

                    // Check for zero-elevation points which indicate incomplete grading.
                    if (fl.ElevationCount > 0)
                    {
                        bool hasZeroElev = false;
                        for (int i = 0; i < fl.ElevationCount; i++)
                        {
                            if (Math.Abs(fl.GetPointAtIndex(i).Z) < 1e-9)
                            {
                                hasZeroElev = true;
                                break;
                            }
                        }
                        if (hasZeroElev && _options.IncludeInfoItems)
                        {
                            _result.AddIssue(MakeIssue(
                                IssueSeverity.Info,
                                IssueCategory.FeatureLine,
                                ObjectType.FeatureLine,
                                handle: handle,
                                name: name,
                                message: $"Feature Line '{name}' has one or more elevation points at Z=0.",
                                detail: "Zero-elevation points may indicate incomplete grade assignments."));
                        }
                    }
                }

                if (_options.IncludeInfoItems && featureLineCount > 0)
                {
                    _result.AddIssue(MakeIssue(
                        IssueSeverity.Info,
                        IssueCategory.FeatureLine,
                        ObjectType.FeatureLine,
                        handle: string.Empty,
                        name: "Feature Lines Summary",
                        message: $"Found {featureLineCount} feature lines; {orphanCount} are orphaned.",
                        detail: string.Empty));
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Phase 11 – Data Shortcuts
        // =====================================================================

        private void ScanDataShortcuts()
        {
            if (_civDoc == null) return;

            try
            {
                // DataShortcutManager may not exist in older Civil API versions.
                var dsMgr = _civDoc.GetShortcutFolder();
                if (string.IsNullOrEmpty(dsMgr))
                {
                    if (_options.IncludeInfoItems)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Info,
                            IssueCategory.DataShortcut,
                            ObjectType.DataShortcut,
                            handle: string.Empty,
                            name: "Data Shortcuts",
                            message: "No Data Shortcut project folder is configured for this drawing.",
                            detail: string.Empty));
                    }
                    return;
                }

                if (!Directory.Exists(dsMgr))
                {
                    _result.AddIssue(MakeIssue(
                        IssueSeverity.Critical,
                        IssueCategory.DataShortcut,
                        ObjectType.DataShortcut,
                        handle: string.Empty,
                        name: "Data Shortcuts",
                        message: $"Data Shortcut folder does not exist: '{dsMgr}'.",
                        detail: "All Data Shortcut references in this drawing are broken. " +
                                "Objects that reference shortcuts will display with missing data.",
                        fix: new FixSuggestion
                        {
                            Title = "Restore the Data Shortcut project folder or update the working folder path.",
                            Description = "In the Toolspace → Prospector tab → right-click Data Shortcuts → " +
                                          "Set Working Folder, and navigate to the correct path.",
                            IsAutomatable = false,
                            EstimatedManualMinutes = 10
                        }));
                    return;
                }

                // Scan for broken shortcut XML files.
                string[] shortcutFiles = Directory.GetFiles(dsMgr, "_Shortcuts.xml",
                                                            SearchOption.AllDirectories);
                if (shortcutFiles.Length == 0 && _options.IncludeInfoItems)
                {
                    _result.AddIssue(MakeIssue(
                        IssueSeverity.Info,
                        IssueCategory.DataShortcut,
                        ObjectType.DataShortcut,
                        handle: string.Empty,
                        name: "Data Shortcuts",
                        message: "No _Shortcuts.xml files found in the Data Shortcut folder.",
                        detail: $"Folder: {dsMgr}"));
                }

                foreach (string xmlFile in shortcutFiles)
                {
                    try
                    {
                        // Validate the XML is well-formed.
                        var doc = System.Xml.Linq.XDocument.Load(xmlFile);

                        // Check referenced source DWG paths inside the shortcuts XML.
                        foreach (var sourceEl in doc.Descendants("SourcePath"))
                        {
                            string sourcePath = sourceEl.Value ?? string.Empty;
                            if (!string.IsNullOrEmpty(sourcePath) && !File.Exists(sourcePath))
                            {
                                _result.AddIssue(MakeIssue(
                                    IssueSeverity.Critical,
                                    IssueCategory.DataShortcut,
                                    ObjectType.DataShortcut,
                                    handle: string.Empty,
                                    name: Path.GetFileName(xmlFile),
                                    message: $"Data Shortcut source DWG not found: '{sourcePath}'.",
                                    detail: $"Shortcut file: {xmlFile}",
                                    fix: new FixSuggestion
                                    {
                                        Title = "Validate Data References (VDR command) to update broken shortcuts.",
                                        IsAutomatable = false,
                                        EstimatedManualMinutes = 15
                                    }));
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        _result.AddIssue(MakeIssue(
                            IssueSeverity.Warning,
                            IssueCategory.DataShortcut,
                            ObjectType.DataShortcut,
                            handle: string.Empty,
                            name: Path.GetFileName(xmlFile),
                            message: $"Data Shortcut file '{xmlFile}' could not be parsed.",
                            detail: ex.Message));
                    }
                }
            }
            catch (System.Exception ex)
            {
                _result.AddIssue(MakeIssue(
                    IssueSeverity.Warning,
                    IssueCategory.DataShortcut,
                    ObjectType.DataShortcut,
                    handle: string.Empty,
                    name: "Data Shortcuts",
                    message: "Data Shortcut scan encountered an error.",
                    detail: ex.Message));
            }
        }

        // =====================================================================
        // Phase 12 – Label Styles
        // =====================================================================

        private void ScanLabelStyles()
        {
            if (_civDoc == null) return;

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                // Walk all entities in model space looking for Civil label entities.
                var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(_db);
                var modelSpace   = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForRead);

                foreach (ObjectId entityId in modelSpace)
                {
                    if (entityId.IsNull || !entityId.IsValid) continue;

                    Entity? ent = null;
                    try { ent = tr.GetObject(entityId, OpenMode.ForRead) as Entity; }
                    catch { continue; }

                    if (ent is not CivDb.Entity civEnt) continue;

                    string handle = entityId.Handle.ToString();
                    string name   = SafeName(civEnt);

                    // Check StyleId validity.
                    try
                    {
                        ObjectId styleId = civEnt.StyleId;
                        if (styleId.IsNull || !styleId.IsValid || styleId.IsErased)
                        {
                            _result.AddIssue(MakeIssue(
                                IssueSeverity.Warning,
                                IssueCategory.Style,
                                ObjectType.LabelStyle,
                                handle: handle,
                                name: name,
                                message: $"Civil 3D entity '{name}' has a missing or erased style reference.",
                                detail: $"StyleId handle: {styleId.Handle}. " +
                                        "The object will display with a default style, which may not meet " +
                                        "the project standards.",
                                fix: new FixSuggestion
                                {
                                    Title = "Reassign a valid style to the object.",
                                    Description = "Right-click the object → Properties → Style → select a valid style.",
                                    IsAutomatable = false,
                                    EstimatedManualMinutes = 2
                                }));
                        }
                    }
                    catch
                    {
                        // StyleId property not supported by this entity type.
                    }
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Phase 13 – Object References
        // =====================================================================

        private void ScanObjectReferences()
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                // Walk the drawing and flag any erased entities in model space
                // that are still referenced by other objects (dangling handles).
                var modelSpaceId = SymbolUtilityServices.GetBlockModelSpaceId(_db);
                var modelSpace   = (BlockTableRecord)tr.GetObject(modelSpaceId, OpenMode.ForRead);

                int danglingCount = 0;
                foreach (ObjectId entityId in modelSpace)
                {
                    if (!entityId.IsValid) continue;

                    if (entityId.IsErased)
                    {
                        danglingCount++;
                        if (_options.IncludeInfoItems)
                        {
                            _result.AddIssue(MakeIssue(
                                IssueSeverity.Info,
                                IssueCategory.Reference,
                                ObjectType.Unknown,
                                handle: entityId.Handle.ToString(),
                                name: "Erased Entity",
                                message: $"Erased entity with handle {entityId.Handle} is still " +
                                         "enumerated in model space.",
                                detail: "This may be benign (AutoCAD deferred cleanup), " +
                                        "but a PURGE + AUDIT is recommended."));
                        }
                    }
                }

                if (danglingCount > 0)
                {
                    _result.AddIssue(MakeIssue(
                        IssueSeverity.Warning,
                        IssueCategory.Reference,
                        ObjectType.Unknown,
                        handle: string.Empty,
                        name: "Database",
                        message: $"Found {danglingCount} erased entities still referenced in model space.",
                        detail: "Run AUDIT and PURGE to clean up the drawing database.",
                        fix: new FixSuggestion
                        {
                            Title  = "Run AUDIT then PURGE.",
                            Description = "In AutoCAD, type AUDIT (All → Yes) followed by PURGE " +
                                         "(purge all). Save the drawing.",
                            IsAutomatable = true,
                            RepairActionType = "Civil3DConnector.Repairs.AuditPurgeRepair",
                            EstimatedManualMinutes = 5
                        }));
                }
            }
            finally
            {
                tr.Commit();
            }
        }

        // =====================================================================
        // Utility helpers
        // =====================================================================

        private static ObjectIssue MakeIssue(
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
