using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace Civil3DConnector.Objects
{
    /// <summary>
    /// Comprehensive handler for Civil 3D TIN and Grid surfaces.
    /// Supports creation, modification, analysis, and volume calculations.
    /// Compatible with Civil 3D 2025–2027.
    /// </summary>
    public class SurfaceHandler : IDisposable
    {
        private readonly Document _doc;
        private readonly Database _db;
        private readonly CivilDocument _civilDoc;
        private bool _disposed;

        public SurfaceHandler(Document document)
        {
            _doc = document ?? throw new ArgumentNullException(nameof(document));
            _db = document.Database;
            _civilDoc = CivilApplication.ActiveDocument;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Surface Creation
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Creates a new empty TIN surface.</summary>
        public ObjectId CreateTinSurface(string name, string layer = "C-TOPO")
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                EnsureLayerExists(layer, tr);

                var surfId = TinSurface.Create(_civilDoc, name);
                var surf = tr.GetObject(surfId, OpenMode.ForWrite) as TinSurface
                    ?? throw new InvalidOperationException("Could not create TIN surface.");

                surf.Layer = layer;
                surf.StyleId = SurfaceStyle.GetDefaultStyleId(_civilDoc);

                tr.Commit();
                return surfId;
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        /// <summary>Creates a surface from a list of 3D points.</summary>
        public ObjectId CreateSurfaceFromPoints(string name, IEnumerable<Point3d> points, string layer = "C-TOPO")
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                EnsureLayerExists(layer, tr);

                var surfId = TinSurface.Create(_civilDoc, name);
                var surf = tr.GetObject(surfId, OpenMode.ForWrite) as TinSurface
                    ?? throw new InvalidOperationException("Could not create TIN surface.");

                surf.Layer = layer;
                var ptList = points.ToList();

                surf.AddVertices(new Point3dCollection(ptList.ToArray()));

                tr.Commit();
                return surfId;
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        /// <summary>Adds breaklines to an existing surface from polylines in the drawing.</summary>
        public void AddBreaklines(ObjectId surfaceId, IEnumerable<ObjectId> polylineIds,
            SurfaceBreaklineType type = SurfaceBreaklineType.Standard, double midOrdinateDist = 1.0)
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var surf = tr.GetObject(surfaceId, OpenMode.ForWrite) as TinSurface
                    ?? throw new ArgumentException("Object is not a TIN surface.");

                var idCol = new ObjectIdCollection();
                foreach (var id in polylineIds)
                    idCol.Add(id);

                surf.BreaklinesDefinition.AddStandardBreaklines(idCol, midOrdinateDist, 0.0, 0.0, 0.0);

                surf.Rebuild();
                tr.Commit();
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        /// <summary>Adds a boundary to the surface.</summary>
        public void AddBoundary(ObjectId surfaceId, ObjectId boundaryPolylineId,
            SurfaceBoundaryType boundaryType = SurfaceBoundaryType.Outer)
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var surf = tr.GetObject(surfaceId, OpenMode.ForWrite) as TinSurface
                    ?? throw new ArgumentException("Object is not a TIN surface.");

                var idCol = new ObjectIdCollection { boundaryPolylineId };
                surf.BoundariesDefinition.AddBoundaries(idCol, 1.0, boundaryType, true);

                surf.Rebuild();
                tr.Commit();
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Surface Analysis
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Gets the elevation at a specific XY location on the surface.</summary>
        public double GetElevationAtPoint(ObjectId surfaceId, double x, double y)
        {
            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var surf = tr.GetObject(surfaceId, OpenMode.ForRead) as Surface
                ?? throw new ArgumentException("Object is not a surface.");

            return surf.FindElevationAtXY(x, y);
        }

        /// <summary>Gets slope (%) at a point on the surface.</summary>
        public double GetSlopeAtPoint(ObjectId surfaceId, double x, double y)
        {
            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var surf = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface
                ?? throw new ArgumentException("Object is not a TIN surface.");

            var vertex = surf.FindClosestVertex(new Point3d(x, y, 0));
            if (vertex == null) return 0.0;
            return vertex.SlopePct;
        }

        /// <summary>Gets comprehensive surface statistics.</summary>
        public SurfaceStatistics GetStatistics(ObjectId surfaceId)
        {
            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var surf = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface
                ?? throw new ArgumentException("Object is not a TIN surface.");

            return new SurfaceStatistics
            {
                Name = surf.Name,
                MinElevation = surf.Minimums.MinimumElevation,
                MaxElevation = surf.Maximums.MaximumElevation,
                MeanElevation = (surf.Minimums.MinimumElevation + surf.Maximums.MaximumElevation) / 2.0,
                SurfaceArea2D = surf.GetIntersectionLinework(null, SurfaceExtractionSettingsType.Model)?.Length ?? 0,
                VertexCount = surf.Vertices.Count,
                TriangleCount = surf.Triangles.Count,
                ExtentsMinX = surf.GeometricExtents.MinPoint.X,
                ExtentsMinY = surf.GeometricExtents.MinPoint.Y,
                ExtentsMaxX = surf.GeometricExtents.MaxPoint.X,
                ExtentsMaxY = surf.GeometricExtents.MaxPoint.Y
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Volume Calculations
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Calculates cut/fill volumes between two surfaces using the composite method.
        /// Commonly used for earthwork calculations in Ecuador (MTOP method).
        /// </summary>
        public VolumeResult CalculateCutFillVolume(ObjectId baseSurfaceId, ObjectId comparisonSurfaceId)
        {
            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var baseSurf = tr.GetObject(baseSurfaceId, OpenMode.ForRead) as TinSurface
                ?? throw new ArgumentException("Base surface is not a TIN surface.");
            var compSurf = tr.GetObject(comparisonSurfaceId, OpenMode.ForRead) as TinSurface
                ?? throw new ArgumentException("Comparison surface is not a TIN surface.");

            var volumeSurf = TinVolumeSurface.Create(_civilDoc, $"Vol_{baseSurf.Name}_{compSurf.Name}",
                baseSurfaceId, comparisonSurfaceId);

            using var tr2 = _db.TransactionManager.StartOpenCloseTransaction();
            var volSurf = tr2.GetObject(volumeSurf, OpenMode.ForRead) as TinVolumeSurface;

            double cut = 0, fill = 0, net = 0;
            if (volSurf != null)
            {
                cut = volSurf.GetCutAndFill().CutVolume;
                fill = volSurf.GetCutAndFill().FillVolume;
                net = volSurf.GetCutAndFill().NetVolume;
            }

            // Clean up temporary volume surface
            using var tr3 = _db.TransactionManager.StartTransaction();
            var obj = tr3.GetObject(volumeSurf, OpenMode.ForWrite);
            obj.Erase();
            tr3.Commit();

            return new VolumeResult
            {
                CutVolume = cut,
                FillVolume = fill,
                NetVolume = net,
                CutArea = 0,
                FillArea = 0
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // Contour Extraction
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Extracts contour lines at specified intervals and creates polylines.</summary>
        public List<ObjectId> ExtractContours(ObjectId surfaceId, double interval, double minorInterval,
            string layer = "C-TOPO-MAJR")
        {
            var contourIds = new List<ObjectId>();

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var surf = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface
                    ?? throw new ArgumentException("Not a TIN surface.");

                var btr = tr.GetObject(_db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord
                    ?? throw new InvalidOperationException("Cannot access current space.");

                EnsureLayerExists(layer, tr);
                EnsureLayerExists("C-TOPO-MINR", tr);

                double minElev = Math.Ceiling(surf.Minimums.MinimumElevation / interval) * interval;
                double maxElev = Math.Floor(surf.Maximums.MaximumElevation / interval) * interval;

                for (double elev = minElev; elev <= maxElev; elev += minorInterval)
                {
                    bool isMajor = Math.Abs(elev % interval) < 0.001;
                    string contourLayer = isMajor ? layer : "C-TOPO-MINR";

                    var contourLines = surf.GetIntersectionLinework(
                        new SurfaceExtractionSettings { ElevationInterval = elev },
                        SurfaceExtractionSettingsType.Model);

                    if (contourLines == null) continue;

                    // Create polylines for contour segments
                    var pl = new Autodesk.AutoCAD.DatabaseServices.Polyline3d(Poly3dType.SimplePoly,
                        new Point3dCollection(), false);
                    pl.Layer = contourLayer;
                    pl.Elevation = elev;

                    btr.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    contourIds.Add(pl.ObjectId);
                }

                tr.Commit();
            }
            catch
            {
                tr.Abort();
                throw;
            }

            return contourIds;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Sample Points Along Alignment
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Samples surface elevations along an alignment at given station interval.
        /// Returns list of (station, elevation) pairs for profile generation.
        /// </summary>
        public List<(double Station, double Elevation)> SampleElevationsAlongAlignment(
            ObjectId surfaceId, ObjectId alignmentId, double stationInterval = 5.0)
        {
            var samples = new List<(double, double)>();

            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var surf = tr.GetObject(surfaceId, OpenMode.ForRead) as Surface
                ?? throw new ArgumentException("Not a surface.");
            var align = tr.GetObject(alignmentId, OpenMode.ForRead) as Alignment
                ?? throw new ArgumentException("Not an alignment.");

            double station = align.StartingStation;
            while (station <= align.EndingStation)
            {
                align.PointLocation(station, 0, out double x, out double y);
                try
                {
                    double elev = surf.FindElevationAtXY(x, y);
                    samples.Add((station, elev));
                }
                catch
                {
                    // Point outside surface extent — skip
                }

                station += stationInterval;
            }

            return samples;
        }

        /// <summary>Creates a profile from sampled surface elevations along an alignment.</summary>
        public ObjectId CreateProfileFromSurface(ObjectId alignmentId, ObjectId surfaceId,
            string profileName, string layer = "C-ROAD-PROF")
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                EnsureLayerExists(layer, tr);

                var align = tr.GetObject(alignmentId, OpenMode.ForRead) as Alignment
                    ?? throw new ArgumentException("Not an alignment.");

                var profileId = Profile.CreateFromSurface(
                    _civilDoc,
                    align.ObjectId,
                    surfaceId,
                    layer,
                    ProfileStyle.GetDefaultStyleId(_civilDoc),
                    ProfileLabelSetStyle.GetDefaultLabelSetStyleId(_civilDoc),
                    profileName);

                tr.Commit();
                return profileId;
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Export
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Exports surface points to CSV. Format: X,Y,Z</summary>
        public void ExportPointsToCsv(ObjectId surfaceId, string csvPath)
        {
            using var tr = _db.TransactionManager.StartOpenCloseTransaction();
            var surf = tr.GetObject(surfaceId, OpenMode.ForRead) as TinSurface
                ?? throw new ArgumentException("Not a TIN surface.");

            using var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
            writer.WriteLine("X,Y,Z");
            foreach (TinSurfaceVertex v in surf.Vertices)
                writer.WriteLine($"{v.Location.X:F4},{v.Location.Y:F4},{v.Location.Z:F4}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

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
    // Result Models
    // ─────────────────────────────────────────────────────────────────────────

    public class SurfaceStatistics
    {
        public string Name { get; set; } = "";
        public double MinElevation { get; set; }
        public double MaxElevation { get; set; }
        public double MeanElevation { get; set; }
        public double SurfaceArea2D { get; set; }
        public int VertexCount { get; set; }
        public int TriangleCount { get; set; }
        public double ExtentsMinX { get; set; }
        public double ExtentsMinY { get; set; }
        public double ExtentsMaxX { get; set; }
        public double ExtentsMaxY { get; set; }
    }

    public class VolumeResult
    {
        public double CutVolume { get; set; }
        public double FillVolume { get; set; }
        public double NetVolume { get; set; }
        public double CutArea { get; set; }
        public double FillArea { get; set; }
        public string Unit { get; set; } = "m³";
    }
}
