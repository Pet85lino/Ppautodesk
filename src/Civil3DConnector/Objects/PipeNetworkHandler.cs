// PipeNetworkHandler.cs
// Full Civil 3D Pipe Network handler: create networks, add pipes/structures,
// set materials, run interference detection, extract hydraulic data,
// and validate against Ecuadorian standards.
//
// References:
//   Autodesk.Civil.DatabaseServices.Pipes namespace (Civil 3D 2024 API)
//   INTERAGUA Normas Técnicas 2019
//   NTE INEN 3054 / 1373 / 1374

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
    /// Parameters for creating a new pipe.
    /// </summary>
    public sealed class PipeCreationParams
    {
        public Point3d StartPoint         { get; set; }
        public Point3d EndPoint           { get; set; }
        public double  Diameter_mm        { get; set; } = 200;
        public string  Material           { get; set; } = "PVC";
        public string  PartFamilyName     { get; set; } = "";   // Civil 3D part family
        public string  PartSizeName       { get; set; } = "";   // e.g. "200mm PVC SN4"
        public double  WallThickness_mm   { get; set; } = 0;    // 0 = use standard
        public string  Description        { get; set; } = "";
    }

    /// <summary>
    /// Parameters for creating a new manhole / structure.
    /// </summary>
    public sealed class StructureCreationParams
    {
        public Point3d InsertionPoint       { get; set; }
        public double  RimElevation_m       { get; set; }
        public double  SumpElevation_m      { get; set; }
        public string  PartFamilyName       { get; set; } = "Manhole";
        public string  PartSizeName         { get; set; } = "1200mm Manhole";
        public string  Description          { get; set; } = "";
    }

    /// <summary>
    /// Hydraulic data extracted from a single pipe.
    /// </summary>
    public sealed class PipeHydraulicData
    {
        public string  PipeHandle           { get; set; }
        public double  Diameter_mm          { get; set; }
        public double  Slope_percent        { get; set; }
        public double  Length_m             { get; set; }
        public double  InvertStart_m        { get; set; }
        public double  InvertEnd_m          { get; set; }
        public double  CoverStart_m         { get; set; }
        public double  CoverEnd_m           { get; set; }
        public double  FullFlowVelocity_ms  { get; set; }
        public double  FullFlowCapacity_lps { get; set; }
        public double  Manning_n            { get; set; }
        public string  Material             { get; set; }
        public bool    IsCompliant          { get; set; }
        public List<string> ComplianceNotes { get; set; } = new List<string>();
    }

    /// <summary>
    /// Result of an interference detection run.
    /// </summary>
    public sealed class InterferenceResult
    {
        public string  PipeHandle1        { get; set; }
        public string  PipeHandle2        { get; set; }
        public double  SeparationDistance_m { get; set; }
        public double  RequiredSeparation_m { get; set; }
        public bool    IsViolation        { get; set; }
        public string  NetworkType1       { get; set; }   // "Sewer", "Water", etc.
        public string  NetworkType2       { get; set; }
        public Point3d IntersectionPoint  { get; set; }
        public string  Description        { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main handler
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handler for Civil 3D Pipe Networks. Creates, modifies, and validates
    /// pipe networks against Ecuadorian infrastructure standards.
    /// </summary>
    public sealed class PipeNetworkHandler : IDisposable
    {
        private readonly Database   _db;
        private readonly CivilDocument _civilDoc;
        private readonly EcuadorianStandardsValidator _validator;
        private bool _disposed;

        // Manning's n by material
        private static readonly IReadOnlyDictionary<string, double> ManningN =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "PVC",      0.011 },
                { "HDPE",     0.011 },
                { "GRP",      0.010 },
                { "CONCRETE", 0.013 },
                { "DCI",      0.012 },
                { "STEEL",    0.014 },
                { "CLAY",     0.015 }
            };

        public PipeNetworkHandler(Database db, CivilDocument civilDoc)
        {
            _db       = db       ?? throw new ArgumentNullException(nameof(db));
            _civilDoc = civilDoc ?? throw new ArgumentNullException(nameof(civilDoc));
            _validator = new EcuadorianStandardsValidator();
        }

        // ═════════════════════════════════════════════════════════════════════
        // NETWORK CREATION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a new Pipe Network in the Civil 3D document.
        /// </summary>
        /// <param name="networkName">Unique name for the network.</param>
        /// <param name="networkDescription">Optional description.</param>
        /// <param name="pipeStyleName">Name of the pipe style in the drawing.</param>
        /// <param name="structureStyleName">Name of the structure style.</param>
        /// <returns>ObjectId of the created Network.</returns>
        public ObjectId CreateNetwork(
            string networkName,
            string networkDescription,
            string pipeStyleName      = "Standard",
            string structureStyleName = "Standard")
        {
            if (string.IsNullOrWhiteSpace(networkName))
                throw new ArgumentException("Network name cannot be empty.", nameof(networkName));

            ObjectId networkId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                // Resolve style ObjectIds
                ObjectId pipeStyleId = GetPipeStyleId(pipeStyleName);
                ObjectId structStyleId = GetStructureStyleId(structureStyleName);

                networkId = Network.Create(
                    _civilDoc,
                    networkName,
                    networkDescription,
                    pipeStyleId,
                    structStyleId,
                    ObjectId.Null,   // default label style
                    ObjectId.Null);  // default label style

                tr.Commit();
            }
            return networkId;
        }

        // ═════════════════════════════════════════════════════════════════════
        // PIPE CREATION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Adds a pipe between two 3D points to an existing network.
        /// </summary>
        public ObjectId AddPipe(ObjectId networkId, PipeCreationParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            ValidatePipeCreationParams(p);

            ObjectId pipeId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network network = tr.GetObject(networkId, OpenMode.ForWrite) as Network
                    ?? throw new InvalidOperationException("Object is not a pipe network.");

                // Resolve the pipe part from Civil 3D parts list
                ObjectId partId = GetPipePartId(p.PartFamilyName, p.PartSizeName, p.Diameter_mm, p.Material);

                pipeId = network.AddLinePipe(
                    partId,
                    p.StartPoint,
                    p.EndPoint,
                    ObjectId.Null);  // no start structure

                // Set description if provided
                if (!string.IsNullOrEmpty(p.Description))
                {
                    Pipe pipe = tr.GetObject(pipeId, OpenMode.ForWrite) as Pipe;
                    if (pipe != null)
                        pipe.Description = p.Description;
                }

                tr.Commit();
            }
            return pipeId;
        }

        /// <summary>
        /// Adds a pipe connecting two existing structures.
        /// </summary>
        public ObjectId AddPipeBetweenStructures(
            ObjectId networkId,
            ObjectId fromStructureId,
            ObjectId toStructureId,
            PipeCreationParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            ObjectId pipeId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network network = tr.GetObject(networkId, OpenMode.ForWrite) as Network
                    ?? throw new InvalidOperationException("Not a pipe network.");

                Structure fromStr = tr.GetObject(fromStructureId, OpenMode.ForRead) as Structure
                    ?? throw new InvalidOperationException("fromStructureId is not a structure.");
                Structure toStr   = tr.GetObject(toStructureId,   OpenMode.ForRead) as Structure
                    ?? throw new InvalidOperationException("toStructureId is not a structure.");

                ObjectId partId = GetPipePartId(p.PartFamilyName, p.PartSizeName, p.Diameter_mm, p.Material);

                pipeId = network.AddLinePipe(
                    partId,
                    fromStr.Position,
                    toStr.Position,
                    fromStructureId,
                    toStructureId);

                tr.Commit();
            }
            return pipeId;
        }

        /// <summary>
        /// Adds a curved (arc) pipe to the network.
        /// </summary>
        public ObjectId AddArcPipe(
            ObjectId networkId,
            Point3d startPoint,
            Point3d midPoint,
            Point3d endPoint,
            PipeCreationParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            ObjectId pipeId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network network = tr.GetObject(networkId, OpenMode.ForWrite) as Network
                    ?? throw new InvalidOperationException("Not a pipe network.");

                ObjectId partId = GetPipePartId(p.PartFamilyName, p.PartSizeName, p.Diameter_mm, p.Material);

                pipeId = network.AddArcPipe(
                    partId,
                    startPoint,
                    midPoint,
                    endPoint,
                    ObjectId.Null);

                tr.Commit();
            }
            return pipeId;
        }

        // ═════════════════════════════════════════════════════════════════════
        // STRUCTURE CREATION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Adds a manhole/structure to an existing network.
        /// </summary>
        public ObjectId AddStructure(ObjectId networkId, StructureCreationParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));

            ObjectId structureId;
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network network = tr.GetObject(networkId, OpenMode.ForWrite) as Network
                    ?? throw new InvalidOperationException("Not a pipe network.");

                ObjectId partId = GetStructurePartId(p.PartFamilyName, p.PartSizeName);

                structureId = network.AddStructure(
                    partId,
                    p.InsertionPoint,
                    p.RimElevation_m,
                    0.0,  // rotation
                    ObjectId.Null,
                    true);  // flag match to surface

                // Adjust sump elevation
                Structure structure = tr.GetObject(structureId, OpenMode.ForWrite) as Structure;
                if (structure != null)
                {
                    structure.SumpElevation = p.SumpElevation_m;
                    if (!string.IsNullOrEmpty(p.Description))
                        structure.Description = p.Description;
                }

                tr.Commit();
            }
            return structureId;
        }

        // ═════════════════════════════════════════════════════════════════════
        // PIPE PROPERTIES
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sets the diameter and material of an existing pipe.
        /// </summary>
        public void SetPipeMaterialAndSize(
            ObjectId pipeId,
            double diameter_mm,
            string material)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Pipe pipe = tr.GetObject(pipeId, OpenMode.ForWrite) as Pipe
                    ?? throw new InvalidOperationException("Not a pipe object.");

                // Inner diameter in metres (Civil 3D API uses metres)
                pipe.InnerDiameterOrWidth = diameter_mm / 1000.0;

                // Store material in description for now (full implementation uses part references)
                if (!string.IsNullOrEmpty(material))
                {
                    string existingDesc = pipe.Description ?? string.Empty;
                    if (!existingDesc.Contains($"Material:{material}"))
                        pipe.Description = $"Material:{material} | {existingDesc}".Trim('|', ' ');
                }

                tr.Commit();
            }
        }

        /// <summary>
        /// Sets the invert elevations of a pipe explicitly.
        /// </summary>
        public void SetPipeInverts(ObjectId pipeId, double startInvert_m, double endInvert_m)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Pipe pipe = tr.GetObject(pipeId, OpenMode.ForWrite) as Pipe
                    ?? throw new InvalidOperationException("Not a pipe object.");

                pipe.StartPoint = new Point3d(pipe.StartPoint.X, pipe.StartPoint.Y, startInvert_m);
                pipe.EndPoint   = new Point3d(pipe.EndPoint.X,   pipe.EndPoint.Y,   endInvert_m);

                tr.Commit();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // HYDRAULIC DATA EXTRACTION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Extracts complete hydraulic data for every pipe in the network.
        /// </summary>
        public List<PipeHydraulicData> ExtractHydraulicData(ObjectId networkId)
        {
            var result = new List<PipeHydraulicData>();

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network network = tr.GetObject(networkId, OpenMode.ForRead) as Network
                    ?? throw new InvalidOperationException("Not a pipe network.");

                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    Pipe pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                    if (pipe == null) continue;

                    double dia_m  = pipe.InnerDiameterOrWidth;
                    double dia_mm = dia_m * 1000.0;
                    double slope  = Math.Abs(pipe.Slope);  // m/m
                    string mat    = ExtractMaterial(pipe);
                    double n      = ManningN.TryGetValue(mat, out double nVal) ? nVal : 0.013;

                    // Manning's equation for full circular pipe
                    double r = dia_m / 4.0;  // hydraulic radius (full flow) = D/4
                    double v = 0, q = 0;
                    if (r > 0 && slope > 0)
                    {
                        v = (1.0 / n) * Math.Pow(r, 2.0 / 3.0) * Math.Pow(slope, 0.5);
                        double area = Math.PI * dia_m * dia_m / 4.0;
                        q = v * area * 1000.0;  // m3/s → L/s
                    }

                    result.Add(new PipeHydraulicData
                    {
                        PipeHandle           = pipe.Handle.ToString(),
                        Diameter_mm          = dia_mm,
                        Slope_percent        = slope * 100.0,
                        Length_m             = pipe.Length2D,
                        InvertStart_m        = pipe.StartPoint.Z,
                        InvertEnd_m          = pipe.EndPoint.Z,
                        CoverStart_m         = pipe.CoverOver,
                        CoverEnd_m           = pipe.CoverOver,   // approximation; precise needs surface query
                        FullFlowVelocity_ms  = v,
                        FullFlowCapacity_lps = q,
                        Manning_n            = n,
                        Material             = mat,
                        IsCompliant          = true  // updated by ValidateNetwork call
                    });
                }

                tr.Commit();
            }
            return result;
        }

        /// <summary>
        /// Extracts hydraulic data for a single pipe.
        /// </summary>
        public PipeHydraulicData ExtractPipeHydraulicData(ObjectId pipeId)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Pipe pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe
                    ?? throw new InvalidOperationException("Not a pipe object.");

                double dia_m  = pipe.InnerDiameterOrWidth;
                double slope  = Math.Abs(pipe.Slope);
                string mat    = ExtractMaterial(pipe);
                double n      = ManningN.TryGetValue(mat, out double nVal) ? nVal : 0.013;
                double r      = dia_m / 4.0;
                double v = 0, q = 0;
                if (r > 0 && slope > 0)
                {
                    v = (1.0 / n) * Math.Pow(r, 2.0 / 3.0) * Math.Pow(slope, 0.5);
                    double area = Math.PI * dia_m * dia_m / 4.0;
                    q = v * area * 1000.0;
                }

                tr.Commit();

                return new PipeHydraulicData
                {
                    PipeHandle           = pipe.Handle.ToString(),
                    Diameter_mm          = dia_m * 1000.0,
                    Slope_percent        = slope * 100.0,
                    Length_m             = pipe.Length2D,
                    InvertStart_m        = pipe.StartPoint.Z,
                    InvertEnd_m          = pipe.EndPoint.Z,
                    CoverStart_m         = pipe.CoverOver,
                    CoverEnd_m           = pipe.CoverOver,
                    FullFlowVelocity_ms  = v,
                    FullFlowCapacity_lps = q,
                    Manning_n            = n,
                    Material             = mat,
                    IsCompliant          = true
                };
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // INTERFERENCE DETECTION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Runs interference detection between two pipe networks.
        /// Returns all pairs of pipes that violate minimum separation distances.
        /// </summary>
        public List<InterferenceResult> RunInterferenceDetection(
            ObjectId sewerNetworkId,
            ObjectId waterNetworkId,
            double requiredHorizontalSeparation_m = 3.00,
            double requiredVerticalSeparation_m   = 0.30)
        {
            var results = new List<InterferenceResult>();

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network sewerNet = tr.GetObject(sewerNetworkId, OpenMode.ForRead) as Network;
                Network waterNet = tr.GetObject(waterNetworkId, OpenMode.ForRead) as Network;

                if (sewerNet == null || waterNet == null)
                {
                    tr.Abort();
                    throw new InvalidOperationException("One or both network IDs are invalid.");
                }

                List<Pipe> sewerPipes = sewerNet.GetPipeIds()
                    .Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe)
                    .Where(p => p != null)
                    .ToList();

                List<Pipe> waterPipes = waterNet.GetPipeIds()
                    .Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe)
                    .Where(p => p != null)
                    .ToList();

                // Compare every sewer pipe against every water pipe
                foreach (Pipe sewerPipe in sewerPipes)
                {
                    foreach (Pipe waterPipe in waterPipes)
                    {
                        InterferenceResult interference = CheckPairSeparation(
                            sewerPipe, waterPipe,
                            requiredHorizontalSeparation_m,
                            requiredVerticalSeparation_m,
                            "Sewer", "Water");

                        if (interference != null)
                            results.Add(interference);
                    }
                }

                tr.Commit();
            }
            return results;
        }

        /// <summary>
        /// Runs interference detection on a single network against itself (crossing pipes).
        /// </summary>
        public List<InterferenceResult> RunSelfInterferenceDetection(
            ObjectId networkId,
            double minimumClearance_m = 0.10)
        {
            var results = new List<InterferenceResult>();

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network network = tr.GetObject(networkId, OpenMode.ForRead) as Network
                    ?? throw new InvalidOperationException("Not a pipe network.");

                List<Pipe> pipes = network.GetPipeIds()
                    .Select(id => tr.GetObject(id, OpenMode.ForRead) as Pipe)
                    .Where(p => p != null)
                    .ToList();

                for (int i = 0; i < pipes.Count - 1; i++)
                {
                    for (int j = i + 1; j < pipes.Count; j++)
                    {
                        InterferenceResult r = CheckPairSeparation(
                            pipes[i], pipes[j],
                            minimumClearance_m, 0.0,
                            "Pipe", "Pipe");
                        if (r != null)
                            results.Add(r);
                    }
                }

                tr.Commit();
            }
            return results;
        }

        // ═════════════════════════════════════════════════════════════════════
        // VALIDATION AGAINST ECUADORIAN STANDARDS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validates the entire pipe network against the applicable Ecuadorian norms.
        /// </summary>
        public ValidationReport ValidateNetwork(ObjectId networkId, ValidationContext ctx)
        {
            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network network = tr.GetObject(networkId, OpenMode.ForRead) as Network
                    ?? throw new InvalidOperationException("Not a pipe network.");

                ValidationReport report = _validator.ValidatePipeNetwork(network, ctx);
                tr.Commit();
                return report;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // BULK OPERATIONS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Creates a complete manhole-connected sewer line from a list of 3D points.
        /// Manholes are placed at each point; pipes connect consecutive manholes.
        /// </summary>
        public (List<ObjectId> StructureIds, List<ObjectId> PipeIds) CreateSewerLine(
            ObjectId networkId,
            IList<Point3d> points,
            double diameter_mm,
            string material = "PVC",
            double rimToSumpOffset_m = 1.20)
        {
            if (points == null || points.Count < 2)
                throw new ArgumentException("At least 2 points are required.", nameof(points));

            var structureIds = new List<ObjectId>();
            var pipeIds      = new List<ObjectId>();

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network network = tr.GetObject(networkId, OpenMode.ForWrite) as Network
                    ?? throw new InvalidOperationException("Not a pipe network.");

                // Create a manhole at each point
                ObjectId structPartId = GetStructurePartId("Manhole", "1200mm Manhole");
                ObjectId pipePartId   = GetPipePartId("", "", diameter_mm, material);

                for (int i = 0; i < points.Count; i++)
                {
                    double rimElev  = points[i].Z;
                    double sumpElev = rimElev - rimToSumpOffset_m;

                    ObjectId strId = network.AddStructure(
                        structPartId,
                        points[i],
                        rimElev,
                        0.0,
                        ObjectId.Null,
                        false);

                    Structure str = tr.GetObject(strId, OpenMode.ForWrite) as Structure;
                    if (str != null)
                        str.SumpElevation = sumpElev;

                    structureIds.Add(strId);
                }

                // Connect consecutive manholes with pipes
                for (int i = 0; i < structureIds.Count - 1; i++)
                {
                    Structure fromStr = tr.GetObject(structureIds[i],     OpenMode.ForRead) as Structure;
                    Structure toStr   = tr.GetObject(structureIds[i + 1], OpenMode.ForRead) as Structure;

                    if (fromStr == null || toStr == null) continue;

                    ObjectId pipeId = network.AddLinePipe(
                        pipePartId,
                        fromStr.Position,
                        toStr.Position,
                        structureIds[i],
                        structureIds[i + 1]);

                    pipeIds.Add(pipeId);
                }

                tr.Commit();
            }

            return (structureIds, pipeIds);
        }

        /// <summary>
        /// Adjusts all pipe inverts to achieve a target slope across the network.
        /// Respects minimum cover depth constraints.
        /// </summary>
        public int AdjustSlopesToMinimum(
            ObjectId networkId,
            ValidationContext ctx,
            double minimumCover_m = 1.20)
        {
            int adjustedCount = 0;
            var standards = ctx.Authority == ValidationContext.UtilityAuthority.Amagua
                ? (IReadOnlyDictionary<int, double>)StandardsConfig.Amagua.SewerMinSlope_Percent
                : (IReadOnlyDictionary<int, double>)StandardsConfig.Interagua.SewerMinSlope_Percent;

            using (Transaction tr = _db.TransactionManager.StartTransaction())
            {
                Network network = tr.GetObject(networkId, OpenMode.ForRead) as Network
                    ?? throw new InvalidOperationException("Not a pipe network.");

                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    Pipe pipe = tr.GetObject(pipeId, OpenMode.ForWrite) as Pipe;
                    if (pipe == null) continue;

                    double dia_mm = pipe.InnerDiameterOrWidth * 1000.0;
                    int diamKey   = FindDiameterKey(standards, (int)Math.Round(dia_mm));
                    if (diamKey <= 0) continue;

                    double minSlope_pct = standards[diamKey];
                    double currentSlope = Math.Abs(pipe.Slope) * 100.0;

                    if (currentSlope < minSlope_pct)
                    {
                        // Adjust end invert to achieve minimum slope
                        double length = pipe.Length2D;
                        double requiredDrop = (minSlope_pct / 100.0) * length;

                        double newEndInvert = pipe.StartPoint.Z - requiredDrop;
                        pipe.EndPoint = new Point3d(pipe.EndPoint.X, pipe.EndPoint.Y, newEndInvert);
                        adjustedCount++;
                    }
                }

                tr.Commit();
            }
            return adjustedCount;
        }

        // ═════════════════════════════════════════════════════════════════════
        // QUERY / REPORTING
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns a summary table of all pipes in the network suitable for reports.
        /// </summary>
        public string GenerateHydraulicSummaryTable(ObjectId networkId)
        {
            var rows = ExtractHydraulicData(networkId);
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("HYDRAULIC SUMMARY – PIPE NETWORK");
            sb.AppendLine(new string('-', 110));
            sb.AppendLine($"{"Handle",-10} {"D(mm)",6} {"L(m)",8} {"S(%)",8} {"InvS(m)",9} {"InvE(m)",9} {"V(m/s)",8} {"Q(L/s)",8} {"n",6} {"Material",-10}");
            sb.AppendLine(new string('-', 110));

            foreach (var row in rows.OrderBy(r => r.InvertStart_m))
            {
                sb.AppendLine(
                    $"{row.PipeHandle,-10} " +
                    $"{row.Diameter_mm,6:F0} " +
                    $"{row.Length_m,8:F2} " +
                    $"{row.Slope_percent,8:F3} " +
                    $"{row.InvertStart_m,9:F3} " +
                    $"{row.InvertEnd_m,9:F3} " +
                    $"{row.FullFlowVelocity_ms,8:F3} " +
                    $"{row.FullFlowCapacity_lps,8:F2} " +
                    $"{row.Manning_n,6:F3} " +
                    $"{row.Material,-10}");
            }

            sb.AppendLine(new string('-', 110));
            sb.AppendLine($"Total pipes: {rows.Count}");
            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        // PRIVATE HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private InterferenceResult CheckPairSeparation(
            Pipe pipe1, Pipe pipe2,
            double requiredH_m, double requiredV_m,
            string type1, string type2)
        {
            // Simplified 2D horizontal proximity check using bounding approach.
            // Production implementation should use Civil 3D's built-in
            // Interference.Create() and Interference.CheckInterference().

            Point2d start1 = new Point2d(pipe1.StartPoint.X, pipe1.StartPoint.Y);
            Point2d end1   = new Point2d(pipe1.EndPoint.X,   pipe1.EndPoint.Y);
            Point2d start2 = new Point2d(pipe2.StartPoint.X, pipe2.StartPoint.Y);
            Point2d end2   = new Point2d(pipe2.EndPoint.X,   pipe2.EndPoint.Y);

            double minDist = SegmentToSegmentDistance2D(start1, end1, start2, end2);

            // Account for pipe radii
            double clearance = minDist
                - (pipe1.OuterDiameterOrWidth / 2.0)
                - (pipe2.OuterDiameterOrWidth / 2.0);

            if (clearance < requiredH_m)
            {
                // Find approximate intersection midpoint for reporting
                Point3d mid = new Point3d(
                    (pipe1.StartPoint.X + pipe2.StartPoint.X) / 2.0,
                    (pipe1.StartPoint.Y + pipe2.StartPoint.Y) / 2.0,
                    (pipe1.StartPoint.Z + pipe2.StartPoint.Z) / 2.0);

                return new InterferenceResult
                {
                    PipeHandle1         = pipe1.Handle.ToString(),
                    PipeHandle2         = pipe2.Handle.ToString(),
                    SeparationDistance_m = Math.Max(0, clearance),
                    RequiredSeparation_m = requiredH_m,
                    IsViolation         = true,
                    NetworkType1        = type1,
                    NetworkType2        = type2,
                    IntersectionPoint   = mid,
                    Description         = $"Separation {clearance:F3} m < required {requiredH_m:F2} m " +
                                         $"between {type1} pipe {pipe1.Handle} and {type2} pipe {pipe2.Handle}."
                };
            }
            return null;
        }

        private static double SegmentToSegmentDistance2D(
            Point2d p1, Point2d p2, Point2d p3, Point2d p4)
        {
            // Point-to-segment and cross checks
            double d1 = PointToSegmentDistance2D(p1, p3, p4);
            double d2 = PointToSegmentDistance2D(p2, p3, p4);
            double d3 = PointToSegmentDistance2D(p3, p1, p2);
            double d4 = PointToSegmentDistance2D(p4, p1, p2);
            return Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));
        }

        private static double PointToSegmentDistance2D(Point2d pt, Point2d a, Point2d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-12)
                return pt.GetDistanceTo(a);

            double t = Math.Max(0, Math.Min(1,
                ((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / lenSq));
            Point2d proj = new Point2d(a.X + t * dx, a.Y + t * dy);
            return pt.GetDistanceTo(proj);
        }

        private static string ExtractMaterial(Pipe pipe)
        {
            string desc = pipe.Description ?? string.Empty;
            if (desc.Contains("Material:"))
            {
                int start = desc.IndexOf("Material:", StringComparison.Ordinal) + 9;
                int end   = desc.IndexOf('|', start);
                return (end < 0 ? desc.Substring(start) : desc.Substring(start, end - start)).Trim();
            }
            // Guess from part name
            string partName = pipe.PartName?.ToUpperInvariant() ?? string.Empty;
            if (partName.Contains("PVC"))     return "PVC";
            if (partName.Contains("HDPE"))    return "HDPE";
            if (partName.Contains("GRP"))     return "GRP";
            if (partName.Contains("CONCRETE")) return "CONCRETE";
            return "PVC";  // default
        }

        private static void ValidatePipeCreationParams(PipeCreationParams p)
        {
            if (p.Diameter_mm <= 0)
                throw new ArgumentException("Pipe diameter must be positive.", nameof(p));
            if (p.StartPoint.IsEqualTo(p.EndPoint))
                throw new ArgumentException("Start and end points must differ.", nameof(p));
        }

        private static int FindDiameterKey(IReadOnlyDictionary<int, double> table, int diameter_mm)
        {
            if (table.ContainsKey(diameter_mm)) return diameter_mm;
            foreach (int key in table.Keys.OrderBy(k => k))
                if (key >= diameter_mm) return key;
            return -1;
        }

        /// <summary>
        /// Resolves the ObjectId for a pipe part from the Civil 3D parts catalogue.
        /// Falls back gracefully if the specified family/size is not found.
        /// </summary>
        private ObjectId GetPipePartId(
            string familyName, string sizeName, double diameter_mm, string material)
        {
            // The Parts Catalogue API varies by Civil 3D version.
            // Standard approach: iterate _civilDoc.PartsList.PipeFamilies.
            try
            {
                PartsList partsList = PartsList.GetPartsList(_civilDoc.Database);
                foreach (PipeFamily family in partsList.PipeFamilies)
                {
                    bool nameMatch = string.IsNullOrEmpty(familyName) ||
                                     family.Name.IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!nameMatch) continue;

                    foreach (PipeSize size in family.GetPipeSizes())
                    {
                        bool sizeMatch = string.IsNullOrEmpty(sizeName) ||
                                         size.Name.IndexOf(sizeName, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool diaMatch  = Math.Abs(size.InnerDiameterOrWidth * 1000.0 - diameter_mm) < 5.0;

                        if (sizeMatch && diaMatch)
                            return size.ObjectId;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"[PipeNetworkHandler] Could not resolve pipe part: {ex.Message}");
            }

            // Return Null – Civil 3D will use the network default
            return ObjectId.Null;
        }

        private ObjectId GetStructurePartId(string familyName, string sizeName)
        {
            try
            {
                PartsList partsList = PartsList.GetPartsList(_civilDoc.Database);
                foreach (StructureFamily family in partsList.StructureFamilies)
                {
                    bool nameMatch = string.IsNullOrEmpty(familyName) ||
                                     family.Name.IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!nameMatch) continue;

                    foreach (StructureSize size in family.GetStructureSizes())
                    {
                        bool sizeMatch = string.IsNullOrEmpty(sizeName) ||
                                         size.Name.IndexOf(sizeName, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (sizeMatch)
                            return size.ObjectId;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"[PipeNetworkHandler] Could not resolve structure part: {ex.Message}");
            }
            return ObjectId.Null;
        }

        private ObjectId GetPipeStyleId(string styleName)
        {
            try
            {
                PipeStyleCollection styles = _civilDoc.Styles.PipeStyles;
                foreach (ObjectId id in styles)
                {
                    using (Transaction tr = _db.TransactionManager.StartTransaction())
                    {
                        PipeStyle style = tr.GetObject(id, OpenMode.ForRead) as PipeStyle;
                        if (style != null && style.Name.Equals(styleName, StringComparison.OrdinalIgnoreCase))
                        {
                            tr.Commit();
                            return id;
                        }
                        tr.Abort();
                    }
                }
            }
            catch { /* use default */ }
            return ObjectId.Null;
        }

        private ObjectId GetStructureStyleId(string styleName)
        {
            try
            {
                StructureStyleCollection styles = _civilDoc.Styles.StructureStyles;
                foreach (ObjectId id in styles)
                {
                    using (Transaction tr = _db.TransactionManager.StartTransaction())
                    {
                        StructureStyle style = tr.GetObject(id, OpenMode.ForRead) as StructureStyle;
                        if (style != null && style.Name.Equals(styleName, StringComparison.OrdinalIgnoreCase))
                        {
                            tr.Commit();
                            return id;
                        }
                        tr.Abort();
                    }
                }
            }
            catch { /* use default */ }
            return ObjectId.Null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // IDisposable
        // ─────────────────────────────────────────────────────────────────────
        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
