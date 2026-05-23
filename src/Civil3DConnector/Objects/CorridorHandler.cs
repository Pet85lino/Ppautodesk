using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DConnector.Objects
{
    /// <summary>
    /// Full handler for Civil 3D Corridor objects.
    /// Supports creating, editing, rebuilding corridors and extracting section data.
    /// Compatible with Civil 3D 2025–2027.
    /// </summary>
    public class CorridorHandler : IDisposable
    {
        private readonly Document _doc;
        private readonly Database _db;
        private readonly CivilDocument _civilDoc;
        private bool _disposed;

        public CorridorHandler(Document document)
        {
            _doc = document ?? throw new ArgumentNullException(nameof(document));
            _db = document.Database;
            _civilDoc = CivilApplication.ActiveDocument;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Creation
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a new corridor from an alignment, profile, and assembly.
        /// This is the standard workflow for road design in Civil 3D.
        /// </summary>
        public ObjectId CreateCorridor(CorridorCreationParams parameters)
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                EnsureLayerExists("C-ROAD-CORR", tr);

                var corridorId = Corridor.Create(_civilDoc, parameters.Name);
                var corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as Corridor
                    ?? throw new InvalidOperationException("Could not create corridor.");

                corridor.Layer = "C-ROAD-CORR";

                // Add baseline
                var baseline = corridor.Baselines.Add(
                    parameters.AlignmentId,
                    parameters.ProfileId);

                // Add baseline region
                var region = baseline.BaselineRegions.Add(
                    parameters.AssemblyId,
                    parameters.StartStation,
                    parameters.EndStation);

                // Configure frequency (station interval)
                region.FrequencyToApplyAssemblies = new FrequencyToApplyAssemblies
                {
                    AlongTangents = parameters.TangentInterval,
                    AlongCurves = parameters.CurveInterval,
                    AlongSpirals = parameters.SpiralInterval,
                    AlongProfileCurves = parameters.ProfileCurveInterval,
                    AtHorizontalGeometryPoints = true,
                    AtProfileGeometryPoints = true,
                    AtSuperelevationCriticalStations = true
                };

                // Add target surface for daylight
                if (parameters.TargetSurfaceId != ObjectId.Null)
                {
                    var targetParams = new CorridorTargetParams();
                    targetParams.SetSurface(parameters.TargetSurfaceId);
                    region.ApplyAssemblyTarget(targetParams);
                }

                // Rebuild the corridor
                corridor.Rebuild();

                tr.Commit();
                return corridorId;
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Analysis and Extraction
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts cross-section data from a corridor at all sampled stations.
        /// Returns offset-elevation pairs per station.
        /// </summary>
        public List<CorridorSection> ExtractCrossSections(ObjectId corridorId, string baselineName = null)
        {
            var sections = new List<CorridorSection>();

            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var corridor = tr.GetObject(corridorId, OpenMode.ForRead) as Corridor
                ?? throw new ArgumentException("Object is not a Corridor.");

            foreach (Baseline baseline in corridor.Baselines)
            {
                if (baselineName != null && baseline.Name != baselineName) continue;

                foreach (BaselineRegion region in baseline.BaselineRegions)
                {
                    foreach (CorridorSection section in region.GetCorridorSections())
                    {
                        var cs = new CorridorSection
                        {
                            BaselineName = baseline.Name,
                            Station = section.Station
                        };

                        foreach (CorridorPoint pt in section.GetCorridorPoints())
                        {
                            cs.Points.Add(new CorridorSectionPoint
                            {
                                Offset = pt.OffsetFromBaseline,
                                Elevation = pt.Elevation,
                                Code = pt.PointCode
                            });
                        }

                        sections.Add(cs);
                    }
                }
            }

            return sections;
        }

        /// <summary>Gets corridor feature lines (ETW, CL, etc.) as 3D polylines.</summary>
        public List<CorridorFeatureLine> GetFeatureLines(ObjectId corridorId)
        {
            var featureLines = new List<CorridorFeatureLine>();

            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var corridor = tr.GetObject(corridorId, OpenMode.ForRead) as Corridor
                ?? throw new ArgumentException("Object is not a Corridor.");

            foreach (Baseline baseline in corridor.Baselines)
            {
                foreach (FeatureLine3dCollection flColl in baseline.GetCorridorFeatureLines())
                {
                    foreach (FeatureLine3d fl in flColl)
                    {
                        var points = new List<Point3d>();
                        foreach (FeatureLine3dPoint pt in fl.FeatureLinePoints)
                            points.Add(pt.XYZ);

                        featureLines.Add(new CorridorFeatureLine
                        {
                            Code = fl.CodeName,
                            BaselineName = baseline.Name,
                            Points = points
                        });
                    }
                }
            }

            return featureLines;
        }

        /// <summary>
        /// Creates a surface from corridor feature lines (e.g., finished grade surface).
        /// </summary>
        public ObjectId CreateSurfaceFromCorridor(ObjectId corridorId, string surfaceName,
            IEnumerable<string> featureLineCodes)
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var corridor = tr.GetObject(corridorId, OpenMode.ForWrite) as Corridor
                    ?? throw new ArgumentException("Object is not a Corridor.");

                var surfId = TinSurface.Create(_civilDoc, surfaceName);
                var surf = tr.GetObject(surfId, OpenMode.ForWrite) as TinSurface;

                if (surf == null) throw new InvalidOperationException("Could not create surface.");

                // Link corridor to surface
                corridor.AddCorridorSurface(surfaceName, surfId);
                var corrSurf = corridor.GetCorridorSurfaces()
                    .FirstOrDefault(s => s.Name == surfaceName);

                if (corrSurf != null)
                {
                    foreach (string code in featureLineCodes)
                        corrSurf.AddFeatureLineCode(code);
                }

                corridor.Rebuild();
                tr.Commit();
                return surfId;
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Validation
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Validates corridor regions, assemblies, and baseline references.</summary>
        public List<string> ValidateCorridor(ObjectId corridorId)
        {
            var issues = new List<string>();

            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var corridor = tr.GetObject(corridorId, OpenMode.ForRead) as Corridor;
            if (corridor == null) { issues.Add("Object is not a corridor."); return issues; }

            if (!corridor.BuildStatusIsUpToDate)
                issues.Add($"Corridor '{corridor.Name}' needs rebuild.");

            foreach (Baseline bl in corridor.Baselines)
            {
                var align = tr.GetObject(bl.AlignmentId, OpenMode.ForRead) as Alignment;
                if (align == null)
                    issues.Add($"Baseline '{bl.Name}' references a missing alignment.");

                var profile = tr.GetObject(bl.ProfileId, OpenMode.ForRead) as Profile;
                if (profile == null)
                    issues.Add($"Baseline '{bl.Name}' references a missing profile.");

                foreach (BaselineRegion region in bl.BaselineRegions)
                {
                    var assembly = tr.GetObject(region.AssemblyId, OpenMode.ForRead) as Assembly;
                    if (assembly == null)
                        issues.Add($"Region [{region.StartStation:F0}–{region.EndStation:F0}] references missing assembly.");

                    if (region.StartStation >= region.EndStation)
                        issues.Add($"Region has invalid station range: {region.StartStation:F0} ≥ {region.EndStation:F0}");
                }
            }

            return issues;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Earthwork Quantities
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Calculates earthwork volumes from corridor cross-sections using prismatoid method.
        /// Follows MTOP Ecuador methodology.
        /// </summary>
        public List<EarthworkStation> CalculateEarthworkVolumes(ObjectId corridorId, double stationInterval = 20.0)
        {
            var stations = new List<EarthworkStation>();
            var sections = ExtractCrossSections(corridorId);

            for (int i = 0; i < sections.Count - 1; i++)
            {
                var s1 = sections[i];
                var s2 = sections[i + 1];

                double dist = s2.Station - s1.Station;
                if (dist <= 0) continue;

                double area1Cut = EstimateCutArea(s1);
                double area1Fill = EstimateFillArea(s1);
                double area2Cut = EstimateCutArea(s2);
                double area2Fill = EstimateFillArea(s2);

                // Prismatoid volume formula: V = (L/6) * (A1 + 4Am + A2)
                double cutVol = (dist / 6.0) * (area1Cut + 4 * ((area1Cut + area2Cut) / 2) + area2Cut);
                double fillVol = (dist / 6.0) * (area1Fill + 4 * ((area1Fill + area2Fill) / 2) + area2Fill);

                stations.Add(new EarthworkStation
                {
                    StartStation = s1.Station,
                    EndStation = s2.Station,
                    CutArea = area1Cut,
                    FillArea = area1Fill,
                    CutVolume = cutVol,
                    FillVolume = fillVol
                });
            }

            return stations;
        }

        private static double EstimateCutArea(CorridorSection section)
        {
            var cutPoints = section.Points.Where(p => p.Code.Contains("Datum") || p.Code.Contains("Cut")).ToList();
            if (cutPoints.Count < 2) return 0;
            return Math.Abs(cutPoints.Sum(p => p.Offset * p.Elevation)) / 2.0;
        }

        private static double EstimateFillArea(CorridorSection section)
        {
            var fillPoints = section.Points.Where(p => p.Code.Contains("Fill") || p.Code.Contains("Hinge")).ToList();
            if (fillPoints.Count < 2) return 0;
            return Math.Abs(fillPoints.Sum(p => p.Offset * p.Elevation)) / 2.0;
        }

        private void EnsureLayerExists(string layerName, Transaction tr)
        {
            var lt = tr.GetObject(_db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (lt == null || lt.Has(layerName)) return;
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = layerName };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Parameter and Result Models
    // ─────────────────────────────────────────────────────────────────────────

    public class CorridorCreationParams
    {
        public string Name { get; set; } = "New Corridor";
        public ObjectId AlignmentId { get; set; }
        public ObjectId ProfileId { get; set; }
        public ObjectId AssemblyId { get; set; }
        public ObjectId TargetSurfaceId { get; set; } = ObjectId.Null;
        public double StartStation { get; set; } = double.NaN;
        public double EndStation { get; set; } = double.NaN;
        public double TangentInterval { get; set; } = 20.0;
        public double CurveInterval { get; set; } = 5.0;
        public double SpiralInterval { get; set; } = 5.0;
        public double ProfileCurveInterval { get; set; } = 5.0;
    }

    public class CorridorSection
    {
        public string BaselineName { get; set; } = "";
        public double Station { get; set; }
        public List<CorridorSectionPoint> Points { get; set; } = new();
    }

    public class CorridorSectionPoint
    {
        public double Offset { get; set; }
        public double Elevation { get; set; }
        public string Code { get; set; } = "";
    }

    public class CorridorFeatureLine
    {
        public string Code { get; set; } = "";
        public string BaselineName { get; set; } = "";
        public List<Point3d> Points { get; set; } = new();
    }

    public class EarthworkStation
    {
        public double StartStation { get; set; }
        public double EndStation { get; set; }
        public double CutArea { get; set; }
        public double FillArea { get; set; }
        public double CutVolume { get; set; }
        public double FillVolume { get; set; }
        public double NetVolume => FillVolume - CutVolume;
    }
}
