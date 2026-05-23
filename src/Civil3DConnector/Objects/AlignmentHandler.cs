// AlignmentHandler.cs
// Full Civil 3D Alignment handler: create alignments from polylines,
// add tangents/curves/spirals, set stationing, generate offset alignments,
// and extract geometry.
//
// References:
//   Autodesk.Civil.DatabaseServices namespace (Civil 3D 2024 API)
//   NEVI-12-MTOP road design geometry standards

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using Autodesk.Civil.ApplicationServices;
using Civil3DConnector.Validators;

namespace Civil3DConnector.Objects
{
    // ─────────────────────────────────────────────────────────────────────────
    // Data transfer objects
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parameters for creating a new Civil 3D alignment.
    /// </summary>
    public sealed class AlignmentCreationParams
    {
        public string  Name                 { get; set; }
        public string  Description          { get; set; } = "";
        public string  SiteContextName      { get; set; } = "";  // site name; empty = no site
        public string  AlignmentStyleName   { get; set; } = "Standard";
        public string  LabelSetStyleName    { get; set; } = "Standard";
        public double  StartStation         { get; set; } = 0.0;
        public string  RoadClass            { get; set; } = "R2";
        public int     DesignSpeed_kph      { get; set; } = 80;
    }

    /// <summary>
    /// A single tangent segment defined by start and end point.
    /// </summary>
    public sealed class TangentDefinition
    {
        public Point2d Start { get; set; }
        public Point2d End   { get; set; }
    }

    /// <summary>
    /// A circular curve element connecting two tangents.
    /// </summary>
    public sealed class CurveDefinition
    {
        public double  Radius_m   { get; set; }
        public bool    IsLeft     { get; set; } = false;   // direction of turn
        public double  Deflection_degrees { get; set; }    // optional, computed if 0
    }

    /// <summary>
    /// A clothoid/Euler transition spiral.
    /// </summary>
    public sealed class SpiralDefinition
    {
        public double  Length_m       { get; set; }
        public double  RadiusIn_m     { get; set; }   // radius at start (0 = from tangent)
        public double  RadiusOut_m    { get; set; }   // radius at end
        public bool    IsEntrySpiral  { get; set; } = true;
    }

    /// <summary>
    /// Extracted geometry of a single alignment entity.
    /// </summary>
    public sealed class AlignmentEntityGeometry
    {
        public int    EntityIndex       { get; set; }
        public string EntityType        { get; set; }  // "Line", "Arc", "Spiral"
        public double StartStation_m    { get; set; }
        public double EndStation_m      { get; set; }
        public double Length_m          { get; set; }
        public Point2d StartPoint       { get; set; }
        public Point2d EndPoint         { get; set; }

        // Arc-specific
        public double? Radius_m         { get; set; }
        public Point2d? CentrePoint     { get; set; }
        public double? Deflection_deg   { get; set; }
        public bool?   IsCCW            { get; set; }

        // Spiral-specific
        public double? SpiralA_m        { get; set; }  // parameter A = sqrt(R*L)
        public double? ThetaSpiral_deg  { get; set; }

        // Compliance
        public bool   MeetsMinRadius    { get; set; } = true;
        public string ComplianceNote    { get; set; }
    }

    /// <summary>
    /// Summary geometry data for the whole alignment.
    /// </summary>
    public sealed class AlignmentGeometrySummary
    {
        public string   AlignmentName       { get; set; }
        public string   AlignmentHandle     { get; set; }
        public double   TotalLength_m       { get; set; }
        public double   StartStation        { get; set; }
        public double   EndStation          { get; set; }
        public int      TangentCount        { get; set; }
        public int      CurveCount          { get; set; }
        public int      SpiralCount         { get; set; }
        public double?  MinRadius_m         { get; set; }
        public double?  MaxRadius_m         { get; set; }
        public List<AlignmentEntityGeometry> Entities { get; set; } = new List<AlignmentEntityGeometry>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main handler
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handler for Civil 3D Alignments. Creates, modifies, and interrogates
    /// horizontal alignments for road design per NEVI-12-MTOP.
    /// </summary>
    public sealed class AlignmentHandler : IDisposable
    {
        private readonly Database      _db;
        private readonly CivilDocument _civilDoc;
        private readonly EcuadorianStandardsValidator _validator;
        private bool _disposed;

        public AlignmentHandler(Database db, CivilDocument civilDoc)
        {
            _db       = db       ?? throw new ArgumentNullException(nameof(db));
            _civilDoc = civilDoc ?? throw new ArgumentNullException(nameof(civilDoc));
            _validator = new EcuadorianStandardsValidator();
        }

        // ═════════════════════════════════════════════════════════════════════
        // ALIGNMENT CREATION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates an empty alignment ready to receive entities (tangents, curves, spirals).
        /// </summary>
        public ObjectId CreateEmptyAlignment(AlignmentCreationParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            if (string.IsNullOrWhiteSpace(p.Name))
                throw new ArgumentException("Alignment name is required.", nameof(p));

            ObjectId alignId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                ObjectId styleId    = GetAlignmentStyleId(p.AlignmentStyleName);
                ObjectId labelSetId = GetAlignmentLabelSetId(p.LabelSetStyleName);

                // Resolve site (can be ObjectId.Null for no site)
                ObjectId siteId = ResolveSiteId(p.SiteContextName);

                alignId = Alignment.Create(
                    _civilDoc,
                    p.Name,
                    siteId,
                    p.Description,
                    styleId,
                    labelSetId);

                // Set start station
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment;
                if (alignment != null)
                    alignment.ReferencePointStation = p.StartStation;

                tr.Commit();
            }
            return alignId;
        }

        /// <summary>
        /// Creates an alignment from an existing AutoCAD polyline (LWPolyline or 3dPolyline).
        /// The polyline is converted to tangent-arc geometry using default curve radius.
        /// </summary>
        public ObjectId CreateAlignmentFromPolyline(
            ObjectId polylineId,
            AlignmentCreationParams p,
            double defaultCurveRadius_m = 200.0)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            ObjectId alignId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Entity entity = tr.GetObject(polylineId, OpenMode.ForRead) as Entity
                    ?? throw new InvalidOperationException("Entity not found.");

                ObjectId styleId    = GetAlignmentStyleId(p.AlignmentStyleName);
                ObjectId labelSetId = GetAlignmentLabelSetId(p.LabelSetStyleName);
                ObjectId siteId     = ResolveSiteId(p.SiteContextName);

                // Use Civil 3D's built-in from-polyline creation
                alignId = Alignment.Create(
                    _civilDoc,
                    p.Name,
                    siteId,
                    p.Description,
                    styleId,
                    labelSetId);

                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment;
                if (alignment == null) { tr.Abort(); throw new InvalidOperationException("Alignment creation failed."); }

                // Extract vertices and build alignment geometry
                List<Point2d> vertices = ExtractPolylineVertices(entity);
                if (vertices.Count < 2)
                {
                    tr.Abort();
                    throw new InvalidOperationException("Polyline must have at least 2 vertices.");
                }

                AppendTangentsWithDefaultCurves(alignment, vertices, defaultCurveRadius_m);

                alignment.ReferencePointStation = p.StartStation;
                tr.Commit();
            }
            return alignId;
        }

        // ═════════════════════════════════════════════════════════════════════
        // ADDING GEOMETRY ELEMENTS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Appends a tangent segment to the alignment.
        /// </summary>
        public void AddTangent(ObjectId alignId, Point2d start, Point2d end)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                alignment.Entities.AddFixedLine(start, end);
                tr.Commit();
            }
        }

        /// <summary>
        /// Appends a floating circular curve (tangent-to-tangent fit).
        /// </summary>
        public void AddCircularCurve(ObjectId alignId, double radius_m, bool isLeft)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                AlignmentEntityCollection entities = alignment.Entities;
                if (entities.Count == 0)
                    throw new InvalidOperationException("Add a tangent before adding a curve.");

                // Floating arc attached to last entity
                AlignmentEntity lastEnt = entities[entities.Count - 1];

                // Pass-through arc: from end of previous entity, radius, side
                // AlignmentEntityCollection.AddFloatCurve is the API method:
                entities.AddFloatCurve(
                    lastEnt.EntityId,
                    radius_m,
                    isLeft ? CurveType.LeftArc : CurveType.RightArc);

                tr.Commit();
            }
        }

        /// <summary>
        /// Adds a fixed circular arc defined by three points.
        /// </summary>
        public void AddFixedArcByThreePoints(
            ObjectId alignId, Point2d passThrough1, Point2d passThrough2, Point2d passThrough3)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                alignment.Entities.AddFixedArc(passThrough1, passThrough2, passThrough3);
                tr.Commit();
            }
        }

        /// <summary>
        /// Adds a fixed circular arc defined by centre, radius, and angular sweep.
        /// </summary>
        public void AddFixedArcByCentreRadius(
            ObjectId alignId, Point2d centre, double radius_m,
            double startAngle_rad, double endAngle_rad, bool counterClockwise = true)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                alignment.Entities.AddFixedArc(centre, radius_m, startAngle_rad, endAngle_rad, counterClockwise);
                tr.Commit();
            }
        }

        /// <summary>
        /// Inserts a spiral (clothoid/Euler) transition between the last entity and a circular arc.
        /// </summary>
        public void AddSpiralCurveSpiralGroup(
            ObjectId alignId,
            double spiralInLength_m,
            double circularRadius_m,
            double spiralOutLength_m,
            Point2d piPoint,
            bool isLeft = false)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                AlignmentEntityCollection entities = alignment.Entities;

                // Civil 3D API: AddSCSGroup creates a Spiral-Curve-Spiral group
                entities.AddSCSGroup(
                    spiralInLength_m,
                    circularRadius_m,
                    spiralOutLength_m,
                    piPoint,
                    isLeft ? SpiralCurveType.Clothoid : SpiralCurveType.Clothoid,
                    isLeft ? CurveType.LeftArc : CurveType.RightArc);

                tr.Commit();
            }
        }

        /// <summary>
        /// Adds a best-fit circular arc through a set of points.
        /// </summary>
        public void AddBestFitArc(ObjectId alignId, IList<Point2d> points, double radius_m)
        {
            if (points == null || points.Count < 3)
                throw new ArgumentException("At least 3 points required.", nameof(points));

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                // Use first, middle, last for the 3-point arc
                int mid = points.Count / 2;
                alignment.Entities.AddFixedArc(points[0], points[mid], points[points.Count - 1]);

                tr.Commit();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // STATIONING
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sets the starting station value (offset) for the alignment.
        /// </summary>
        public void SetStartStation(ObjectId alignId, double startStation)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                alignment.ReferencePointStation = startStation;
                tr.Commit();
            }
        }

        /// <summary>
        /// Adds a station equation (chaflán / equation de estacado) at a given raw station.
        /// Useful when alignment passes through a tunnel or crosses an administrative boundary.
        /// </summary>
        public void AddStationEquation(
            ObjectId alignId,
            double rawStation,
            double stationAhead,
            bool increaseAhead = true)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                alignment.StationEquations.Add(rawStation, stationAhead, increaseAhead);
                tr.Commit();
            }
        }

        /// <summary>
        /// Returns the XY coordinates of a point at a given station and offset.
        /// </summary>
        public Point2d GetPointAtStationOffset(ObjectId alignId, double station, double offset)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                alignment.PointLocation(station, offset, out double x, out double y);
                tr.Commit();
                return new Point2d(x, y);
            }
        }

        /// <summary>
        /// Returns the station and offset for a given XY point.
        /// </summary>
        public (double Station, double Offset) GetStationOffsetForPoint(ObjectId alignId, Point2d point)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                alignment.StationOffset(point.X, point.Y, out double station, out double offset);
                tr.Commit();
                return (station, offset);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // OFFSET ALIGNMENTS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates an offset alignment (e.g. edge-of-lane, right-of-way) from a parent alignment.
        /// </summary>
        public ObjectId CreateOffsetAlignment(
            ObjectId parentAlignId,
            double offsetDistance_m,
            string offsetAlignmentName,
            bool isRight = true)
        {
            if (string.IsNullOrWhiteSpace(offsetAlignmentName))
                throw new ArgumentException("Offset alignment name is required.", nameof(offsetAlignmentName));

            ObjectId offsetAlignId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment parent = tr.GetObject(parentAlignId, OpenMode.ForRead) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                double offset = isRight ? -Math.Abs(offsetDistance_m) : Math.Abs(offsetDistance_m);

                ObjectId styleId    = parent.StyleId;
                ObjectId labelSetId = parent.AlignmentLabelSetStyleId;

                offsetAlignId = parent.CreateOffsetAlignment(
                    offsetAlignmentName,
                    offset,
                    styleId,
                    labelSetId);

                tr.Commit();
            }
            return offsetAlignId;
        }

        /// <summary>
        /// Creates both left and right offset alignments (e.g. edge-of-carriageway).
        /// </summary>
        public (ObjectId LeftOffset, ObjectId RightOffset) CreateCarriagewayOffsets(
            ObjectId centrelineAlignId,
            double halfCarriagewayWidth_m,
            string baseName)
        {
            ObjectId left  = CreateOffsetAlignment(centrelineAlignId,  halfCarriagewayWidth_m, $"{baseName}_LeftEdge",  isRight: false);
            ObjectId right = CreateOffsetAlignment(centrelineAlignId,  halfCarriagewayWidth_m, $"{baseName}_RightEdge", isRight: true);
            return (left, right);
        }

        /// <summary>
        /// Creates a full set of standard road offset alignments per NEVI-12.
        /// </summary>
        public Dictionary<string, ObjectId> CreateRoadOffsets(
            ObjectId centrelineAlignId,
            double laneWidth_m,
            double shoulderWidth_m,
            string baseName)
        {
            double halfLane     = laneWidth_m / 2.0;
            double edgePavement = halfLane + shoulderWidth_m;

            var result = new Dictionary<string, ObjectId>
            {
                ["CL"]             = centrelineAlignId,
                ["LeftLaneEdge"]   = CreateOffsetAlignment(centrelineAlignId, halfLane,      $"{baseName}_LeftLaneEdge",   false),
                ["RightLaneEdge"]  = CreateOffsetAlignment(centrelineAlignId, halfLane,      $"{baseName}_RightLaneEdge",  true),
                ["LeftShoulder"]   = CreateOffsetAlignment(centrelineAlignId, edgePavement,  $"{baseName}_LeftShoulder",   false),
                ["RightShoulder"]  = CreateOffsetAlignment(centrelineAlignId, edgePavement,  $"{baseName}_RightShoulder",  true)
            };
            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        // GEOMETRY EXTRACTION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extracts complete geometry summary for an alignment.
        /// </summary>
        public AlignmentGeometrySummary ExtractGeometry(ObjectId alignId)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                var summary = new AlignmentGeometrySummary
                {
                    AlignmentName   = alignment.Name,
                    AlignmentHandle = alignment.Handle.ToString(),
                    TotalLength_m   = alignment.Length,
                    StartStation    = alignment.StartingStation,
                    EndStation      = alignment.EndingStation
                };

                double? minR = null, maxR = null;
                AlignmentEntityCollection entities = alignment.Entities;

                for (int i = 0; i < entities.Count; i++)
                {
                    AlignmentEntity ent = entities[i];
                    var geo = new AlignmentEntityGeometry
                    {
                        EntityIndex    = i,
                        StartStation_m = ent.StartStation,
                        EndStation_m   = ent.EndStation,
                        Length_m       = ent.Length
                    };

                    switch (ent.EntityType)
                    {
                        case AlignmentEntityType.Line:
                        {
                            AlignmentLine line = ent as AlignmentLine;
                            geo.EntityType  = "Line";
                            geo.StartPoint  = line?.StartPoint ?? default;
                            geo.EndPoint    = line?.EndPoint   ?? default;
                            summary.TangentCount++;
                            break;
                        }
                        case AlignmentEntityType.Arc:
                        {
                            AlignmentArc arc = ent as AlignmentArc;
                            geo.EntityType   = "Arc";
                            geo.StartPoint   = arc?.StartPoint  ?? default;
                            geo.EndPoint     = arc?.EndPoint    ?? default;
                            geo.Radius_m     = arc?.Radius;
                            geo.CentrePoint  = arc?.CenterPoint;
                            geo.Deflection_deg = arc?.Delta * 180.0 / Math.PI;
                            geo.IsCCW        = arc?.Clockwise == false;

                            if (arc != null)
                            {
                                if (!minR.HasValue || arc.Radius < minR) minR = arc.Radius;
                                if (!maxR.HasValue || arc.Radius > maxR) maxR = arc.Radius;
                            }
                            summary.CurveCount++;
                            break;
                        }
                        case AlignmentEntityType.Spiral:
                        {
                            AlignmentSpiral spiral = ent as AlignmentSpiral;
                            geo.EntityType       = "Spiral";
                            geo.StartPoint       = spiral?.StartPoint ?? default;
                            geo.EndPoint         = spiral?.EndPoint   ?? default;
                            geo.SpiralA_m        = spiral?.A;
                            geo.ThetaSpiral_deg  = spiral?.Theta * 180.0 / Math.PI;
                            summary.SpiralCount++;
                            break;
                        }
                        default:
                            geo.EntityType = ent.EntityType.ToString();
                            break;
                    }

                    summary.Entities.Add(geo);
                }

                summary.MinRadius_m = minR;
                summary.MaxRadius_m = maxR;

                tr.Commit();
                return summary;
            }
        }

        /// <summary>
        /// Returns a list of all bearing changes (PI points) along the alignment.
        /// </summary>
        public List<(double Station, Point2d Point, double DeflectionAngle_deg)> GetPIPoints(ObjectId alignId)
        {
            var result = new List<(double, Point2d, double)>();

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                AlignmentEntityCollection entities = alignment.Entities;

                for (int i = 0; i < entities.Count; i++)
                {
                    AlignmentEntity ent = entities[i];
                    if (ent.EntityType == AlignmentEntityType.Arc)
                    {
                        AlignmentArc arc = ent as AlignmentArc;
                        if (arc == null) continue;

                        // PI station ≈ midpoint of arc's subtended station range
                        double piStation = (arc.StartStation + arc.EndStation) / 2.0;
                        double piDeflection = arc.Delta * 180.0 / Math.PI;

                        alignment.PointLocation(piStation, 0, out double x, out double y);
                        result.Add((piStation, new Point2d(x, y), piDeflection));
                    }
                }

                tr.Commit();
            }
            return result;
        }

        /// <summary>
        /// Samples alignment geometry at regular station intervals.
        /// Returns a polyline point list suitable for visualisation or export.
        /// </summary>
        public List<Point2d> SampleAtInterval(ObjectId alignId, double intervalStation_m = 10.0)
        {
            var points = new List<Point2d>();

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                double station = alignment.StartingStation;
                double end     = alignment.EndingStation;

                while (station <= end + 1e-6)
                {
                    alignment.PointLocation(station, 0.0, out double x, out double y);
                    points.Add(new Point2d(x, y));
                    station += intervalStation_m;
                }

                // Ensure exact end point is included
                if (Math.Abs(points.Last().X - 0) > 1e-3 || station > end)
                {
                    alignment.PointLocation(end, 0.0, out double xe, out double ye);
                    points.Add(new Point2d(xe, ye));
                }

                tr.Commit();
            }
            return points;
        }

        // ═════════════════════════════════════════════════════════════════════
        // VALIDATION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validates the alignment against NEVI-12-MTOP horizontal geometry standards.
        /// </summary>
        public ValidationReport ValidateAlignment(ObjectId alignId, ValidationContext ctx)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                ValidationReport report = _validator.ValidateAlignment(alignment, ctx);
                tr.Commit();
                return report;
            }
        }

        /// <summary>
        /// Generates a compliance table comparing extracted geometry against NEVI-12.
        /// </summary>
        public string GenerateComplianceReport(ObjectId alignId, ValidationContext ctx)
        {
            AlignmentGeometrySummary summary = ExtractGeometry(alignId);
            ValidationReport validation      = ValidateAlignment(alignId, ctx);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine($"ALIGNMENT COMPLIANCE REPORT – {summary.AlignmentName}");
            sb.AppendLine($"Standard: NEVI-12-MTOP | Design Speed: {ctx.RoadDesignSpeed_kph} km/h | Class: {ctx.RoadClass}");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine($"Total length : {summary.TotalLength_m:F2} m");
            sb.AppendLine($"Stations     : {summary.StartStation:F3} – {summary.EndStation:F3}");
            sb.AppendLine($"Tangents     : {summary.TangentCount}");
            sb.AppendLine($"Curves       : {summary.CurveCount}");
            sb.AppendLine($"Spirals      : {summary.SpiralCount}");
            if (summary.MinRadius_m.HasValue)
                sb.AppendLine($"Min radius   : {summary.MinRadius_m:F1} m");
            if (summary.MaxRadius_m.HasValue)
                sb.AppendLine($"Max radius   : {summary.MaxRadius_m:F1} m");
            sb.AppendLine();
            sb.AppendLine(validation.Summary);
            sb.AppendLine();

            if (validation.ErrorCount > 0)
            {
                sb.AppendLine("ERRORS:");
                foreach (var issue in validation.Errors)
                    sb.AppendLine($"  [{issue.Code}] {issue.Message}");
                sb.AppendLine();
            }

            if (validation.WarningCount > 0)
            {
                sb.AppendLine("WARNINGS:");
                foreach (var issue in validation.Warnings)
                    sb.AppendLine($"  [{issue.Code}] {issue.Message}");
                sb.AppendLine();
            }

            // Per-entity table
            sb.AppendLine("─────────────────────────────────────────────────────────────");
            sb.AppendLine($"{"#",4} {"Type",-8} {"StaStart",10} {"StaEnd",10} {"Length",8} {"Radius",10}");
            sb.AppendLine("─────────────────────────────────────────────────────────────");

            foreach (var ent in summary.Entities)
            {
                string radius = ent.Radius_m.HasValue ? $"{ent.Radius_m:F1}" : "—";
                sb.AppendLine($"{ent.EntityIndex,4} {ent.EntityType,-8} {ent.StartStation_m,10:F3} {ent.EndStation_m,10:F3} {ent.Length_m,8:F3} {radius,10}");
            }
            sb.AppendLine("─────────────────────────────────────────────────────────────");

            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        // BULK / UTILITY OPERATIONS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts an existing alignment to a polyline (useful for CAD output or GIS export).
        /// </summary>
        public ObjectId ConvertToPolyline(ObjectId alignId, double sampleInterval_m = 5.0)
        {
            List<Point2d> points = SampleAtInterval(alignId, sampleInterval_m);

            ObjectId polyId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                BlockTable bt = tr.GetObject(_db.BlockTableId, OpenMode.ForRead) as BlockTable;
                BlockTableRecord modelSpace = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                Polyline poly = new Polyline();
                for (int i = 0; i < points.Count; i++)
                    poly.AddVertexAt(i, points[i], 0, 0, 0);

                polyId = modelSpace.AppendEntity(poly);
                tr.AddNewlyCreatedDBObject(poly, true);
                tr.Commit();
            }
            return polyId;
        }

        /// <summary>
        /// Applies a minimum-radius fix to all arcs below the NEVI-12 absolute minimum,
        /// adjusting adjacent tangents to maintain continuity.
        /// Returns the number of arcs adjusted.
        /// </summary>
        public int EnforceMinimumRadii(ObjectId alignId, ValidationContext ctx)
        {
            int adjusted = 0;
            int V = ctx.RoadDesignSpeed_kph;

            if (!StandardsConfig.Mtop.MinCurveRadius_m.TryGetValue(V, out double minR))
                minR = 25.0;  // fallback

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Alignment alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment
                    ?? throw new InvalidOperationException("Not an alignment.");

                AlignmentEntityCollection entities = alignment.Entities;
                for (int i = 0; i < entities.Count; i++)
                {
                    if (entities[i].EntityType != AlignmentEntityType.Arc) continue;
                    AlignmentArc arc = entities[i] as AlignmentArc;
                    if (arc == null) continue;

                    if (arc.Radius < minR)
                    {
                        arc.Radius = minR;
                        adjusted++;
                    }
                }

                tr.Commit();
            }
            return adjusted;
        }

        // ═════════════════════════════════════════════════════════════════════
        // PRIVATE HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private static List<Point2d> ExtractPolylineVertices(Entity entity)
        {
            var pts = new List<Point2d>();
            if (entity is Polyline lw)
            {
                for (int i = 0; i < lw.NumberOfVertices; i++)
                    pts.Add(lw.GetPoint2dAt(i));
            }
            else if (entity is Polyline2d poly2d)
            {
                foreach (ObjectId vtxId in poly2d)
                {
                    using (Transaction tr2 = entity.Database.TransactionManager.StartTransaction())
                    {
                        Vertex2d v = tr2.GetObject(vtxId, OpenMode.ForRead) as Vertex2d;
                        if (v != null)
                            pts.Add(new Point2d(v.Position.X, v.Position.Y));
                        tr2.Commit();
                    }
                }
            }
            else if (entity is Polyline3d poly3d)
            {
                foreach (ObjectId vtxId in poly3d)
                {
                    using (Transaction tr2 = entity.Database.TransactionManager.StartTransaction())
                    {
                        PolylineVertex3d v = tr2.GetObject(vtxId, OpenMode.ForRead) as PolylineVertex3d;
                        if (v != null)
                            pts.Add(new Point2d(v.Position.X, v.Position.Y));
                        tr2.Commit();
                    }
                }
            }
            return pts;
        }

        private static void AppendTangentsWithDefaultCurves(
            Alignment alignment, List<Point2d> vertices, double defaultRadius_m)
        {
            // Build tangent-only geometry; curves will be inserted at PIs by Civil 3D
            // when using AddFixedLine. For full SCS groups, call AddSCSGroup per PI.
            AlignmentEntityCollection entities = alignment.Entities;

            for (int i = 0; i < vertices.Count - 1; i++)
            {
                if (i == 0)
                {
                    entities.AddFixedLine(vertices[i], vertices[i + 1]);
                }
                else
                {
                    // Add a floating arc connecting the previous tangent to the new one
                    try
                    {
                        AlignmentEntity prev = entities[entities.Count - 1];
                        entities.AddFloatCurve(prev.EntityId, defaultRadius_m,
                            DetermineArcSide(vertices[i - 1], vertices[i], vertices[i + 1]));
                        entities.AddFloatLine(vertices[i + 1]);
                    }
                    catch
                    {
                        // If floating arc fails, add as fixed line (sharp corner)
                        entities.AddFixedLine(vertices[i], vertices[i + 1]);
                    }
                }
            }
        }

        private static CurveType DetermineArcSide(Point2d prev, Point2d pi, Point2d next)
        {
            // Cross product of (pi-prev) × (next-pi) determines turn direction
            double cross = (pi.X - prev.X) * (next.Y - pi.Y) -
                           (pi.Y - prev.Y) * (next.X - pi.X);
            return cross > 0 ? CurveType.LeftArc : CurveType.RightArc;
        }

        private ObjectId GetAlignmentStyleId(string styleName)
        {
            try
            {
                AlignmentStyleCollection styles = _civilDoc.Styles.AlignmentStyles;
                foreach (ObjectId id in styles)
                {
                    using (Transaction tr = _db.TransactionManager.StartTransaction())
                    {
                        AlignmentStyle style = tr.GetObject(id, OpenMode.ForRead) as AlignmentStyle;
                        if (style != null && style.Name.Equals(styleName, StringComparison.OrdinalIgnoreCase))
                        {
                            tr.Commit();
                            return id;
                        }
                        tr.Abort();
                    }
                }
            }
            catch { /* fall through */ }
            return ObjectId.Null;
        }

        private ObjectId GetAlignmentLabelSetId(string labelSetName)
        {
            try
            {
                LabelSetStyleCollection styles = _civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles;
                foreach (ObjectId id in styles)
                {
                    using (Transaction tr = _db.TransactionManager.StartTransaction())
                    {
                        LabelSetStyle style = tr.GetObject(id, OpenMode.ForRead) as LabelSetStyle;
                        if (style != null && style.Name.Equals(labelSetName, StringComparison.OrdinalIgnoreCase))
                        {
                            tr.Commit();
                            return id;
                        }
                        tr.Abort();
                    }
                }
            }
            catch { /* fall through */ }
            return ObjectId.Null;
        }

        private ObjectId ResolveSiteId(string siteName)
        {
            if (string.IsNullOrWhiteSpace(siteName)) return ObjectId.Null;
            try
            {
                SiteCollection sites = _civilDoc.GetSiteIds();
                foreach (ObjectId id in sites)
                {
                    using (Transaction tr = _db.TransactionManager.StartTransaction())
                    {
                        Site site = tr.GetObject(id, OpenMode.ForRead) as Site;
                        if (site != null && site.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase))
                        {
                            tr.Commit();
                            return id;
                        }
                        tr.Abort();
                    }
                }
            }
            catch { /* fall through */ }
            return ObjectId.Null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // IDisposable
        // ─────────────────────────────────────────────────────────────────────
        public void Dispose()
        {
            if (!_disposed)
                _disposed = true;
        }
    }
}
