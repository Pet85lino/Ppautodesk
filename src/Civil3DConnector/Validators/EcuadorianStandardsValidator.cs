// EcuadorianStandardsValidator.cs
// Main validator implementing all Ecuadorian engineering norms for Civil 3D objects.
// Covers: NEVI-12-MTOP, NEC-SE-DS, NTE INEN 3054/1373/1374, INTERAGUA, AMAGUA, Ordenanzas municipales.
//
// Usage:
//   var validator = new EcuadorianStandardsValidator();
//   ValidationReport report = validator.ValidatePipeNetwork(network, context);

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;

namespace Civil3DConnector.Validators
{
    // ─────────────────────────────────────────────────────────────────────────
    // Severity of a validation finding
    // ─────────────────────────────────────────────────────────────────────────
    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error
    }

    // ─────────────────────────────────────────────────────────────────────────
    // A single validation finding
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class ValidationIssue
    {
        public ValidationSeverity Severity   { get; }
        public string             Code       { get; }   // e.g. "SLOPE-001"
        public string             Category   { get; }   // e.g. "Pipe Slope"
        public string             Message    { get; }
        public string             ElementId  { get; }   // Civil 3D object handle
        public string             Standard   { get; }   // e.g. "INTERAGUA 2019"
        public double?            ActualValue   { get; }
        public double?            LimitValue    { get; }
        public string             Units      { get; }

        public ValidationIssue(
            ValidationSeverity severity,
            string code,
            string category,
            string message,
            string elementId,
            string standard,
            double? actual  = null,
            double? limit   = null,
            string  units   = "")
        {
            Severity    = severity;
            Code        = code;
            Category    = category;
            Message     = message;
            ElementId   = elementId;
            Standard    = standard;
            ActualValue = actual;
            LimitValue  = limit;
            Units       = units;
        }

        public override string ToString() =>
            $"[{Severity}] {Code} – {Category}: {Message}" +
            (ActualValue.HasValue ? $" (actual={ActualValue:F4} {Units}, limit={LimitValue:F4} {Units})" : string.Empty);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Summary report returned by each Validate* method
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class ValidationReport
    {
        private readonly List<ValidationIssue> _issues = new List<ValidationIssue>();

        public IReadOnlyList<ValidationIssue> Issues         => _issues;
        public bool IsValid                                   => _issues.All(i => i.Severity != ValidationSeverity.Error);
        public int  ErrorCount                                => _issues.Count(i => i.Severity == ValidationSeverity.Error);
        public int  WarningCount                              => _issues.Count(i => i.Severity == ValidationSeverity.Warning);
        public string Summary                                 =>
            $"Validation result: {(IsValid ? "PASS" : "FAIL")} – {ErrorCount} error(s), {WarningCount} warning(s)";

        internal void Add(ValidationIssue issue) => _issues.Add(issue);
        internal void AddRange(IEnumerable<ValidationIssue> issues) => _issues.AddRange(issues);

        public IEnumerable<ValidationIssue> Errors   => _issues.Where(i => i.Severity == ValidationSeverity.Error);
        public IEnumerable<ValidationIssue> Warnings => _issues.Where(i => i.Severity == ValidationSeverity.Warning);
        public IEnumerable<ValidationIssue> Infos    => _issues.Where(i => i.Severity == ValidationSeverity.Info);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Context supplied to the validator (city / authority selection)
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class ValidationContext
    {
        public enum UtilityAuthority { Interagua, Amagua, Etapa, Other }
        public enum CityContext      { Guayaquil, Quito, Cuenca, Other }
        public enum TerrainType      { Plano, Ondulado, Montanoso, Escarpado }

        public UtilityAuthority Authority         { get; set; } = UtilityAuthority.Interagua;
        public CityContext      City              { get; set; } = CityContext.Guayaquil;
        public TerrainType      Terrain           { get; set; } = TerrainType.Plano;
        public string           SeismicZone       { get; set; } = "V";
        public int              RoadDesignSpeed_kph { get; set; } = 80;
        public string           RoadClass         { get; set; } = "R2";   // E, R1, R2, R3, U_A, U_C, U_L
        public bool             IsUrbanArea       { get; set; } = false;
        public bool             StrictMode        { get; set; } = false;  // treat warnings as errors
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main validator class
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class EcuadorianStandardsValidator
    {
        private readonly StandardsConfig.InteraguaStandards _interagua = StandardsConfig.Interagua;
        private readonly StandardsConfig.AmagualStandards   _amagua    = StandardsConfig.Amagua;
        private readonly StandardsConfig.MtopStandards       _mtop      = StandardsConfig.Mtop;
        private readonly StandardsConfig.NecStandards         _nec       = StandardsConfig.Nec;
        private readonly StandardsConfig.NteInenStandards     _inen      = StandardsConfig.NteInen;

        // ═════════════════════════════════════════════════════════════════════
        // PUBLIC ENTRY POINTS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validates a Civil 3D pipe network against the applicable Ecuadorian standards.
        /// </summary>
        public ValidationReport ValidatePipeNetwork(Network network, ValidationContext ctx)
        {
            if (network == null) throw new ArgumentNullException(nameof(network));
            if (ctx     == null) throw new ArgumentNullException(nameof(ctx));

            var report = new ValidationReport();
            using (Transaction tr = network.Database.TransactionManager.StartTransaction())
            {
                // Validate every pipe in the network
                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    Pipe pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                    if (pipe == null) continue;
                    report.AddRange(ValidateSinglePipe(pipe, ctx));
                }

                // Validate every structure (manhole, inlet, etc.)
                foreach (ObjectId structId in network.GetStructureIds())
                {
                    Structure structure = tr.GetObject(structId, OpenMode.ForRead) as Structure;
                    if (structure == null) continue;
                    report.AddRange(ValidateSingleStructure(structure, ctx));
                }

                // Network-level checks
                report.AddRange(ValidateNetworkSeparations(network, tr, ctx));

                tr.Commit();
            }
            return report;
        }

        /// <summary>
        /// Validates a Civil 3D Alignment against MTOP road design standards.
        /// </summary>
        public ValidationReport ValidateAlignment(Alignment alignment, ValidationContext ctx)
        {
            if (alignment == null) throw new ArgumentNullException(nameof(alignment));
            if (ctx       == null) throw new ArgumentNullException(nameof(ctx));

            var report = new ValidationReport();
            report.AddRange(ValidateHorizontalCurves(alignment, ctx));
            report.AddRange(ValidateAlignmentTangents(alignment, ctx));
            return report;
        }

        /// <summary>
        /// Validates a profile (vertical alignment) against MTOP standards.
        /// </summary>
        public ValidationReport ValidateProfile(Profile profile, ValidationContext ctx)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (ctx     == null) throw new ArgumentNullException(nameof(ctx));

            var report = new ValidationReport();
            report.AddRange(ValidateProfileGrades(profile, ctx));
            report.AddRange(ValidateVerticalCurves(profile, ctx));
            return report;
        }

        /// <summary>
        /// Validates a corridor cross-section assembly against MTOP standards.
        /// </summary>
        public ValidationReport ValidateCorridorCrossSection(
            double laneWidth_m,
            double shoulderWidth_m,
            double superelevation_percent,
            ValidationContext ctx)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var report = new ValidationReport();
            report.AddRange(ValidateCrossSection(laneWidth_m, shoulderWidth_m, superelevation_percent, ctx));
            return report;
        }

        // ═════════════════════════════════════════════════════════════════════
        // PIPE VALIDATION METHODS
        // ═════════════════════════════════════════════════════════════════════

        private IEnumerable<ValidationIssue> ValidateSinglePipe(Pipe pipe, ValidationContext ctx)
        {
            var issues = new List<ValidationIssue>();
            string handle = pipe.Handle.ToString();

            double diameter_mm = pipe.InnerDiameterOrWidth * 1000.0;  // Civil 3D returns metres
            double slope_pct   = Math.Abs(pipe.Slope) * 100.0;        // dimensionless → percent
            double length_m    = pipe.Length2D;

            // ── 1. Minimum diameter ──────────────────────────────────────────
            issues.AddRange(ValidatePipeDiameter(pipe, diameter_mm, ctx, handle));

            // ── 2. Slope ─────────────────────────────────────────────────────
            issues.AddRange(ValidatePipeSlope(pipe, slope_pct, diameter_mm, ctx, handle));

            // ── 3. Cover depth ───────────────────────────────────────────────
            issues.AddRange(ValidatePipeCover(pipe, ctx, handle));

            // ── 4. Hydraulic velocity (Manning's equation) ──────────────────
            issues.AddRange(ValidateHydraulicVelocity(pipe, diameter_mm, slope_pct, ctx, handle));

            // ── 5. Maximum pipe length between manholes ──────────────────────
            if (length_m > 120.0)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning, "PIPE-005", "Pipe Length",
                    $"Pipe length {length_m:F1} m exceeds maximum manhole spacing of 120 m.",
                    handle, "INTERAGUA 2019 §4.3.2",
                    length_m, 120.0, "m"));

            return issues;
        }

        private IEnumerable<ValidationIssue> ValidatePipeDiameter(
            Pipe pipe, double diameter_mm, ValidationContext ctx, string handle)
        {
            var issues = new List<ValidationIssue>();

            // Choose the applicable standard
            double minDia = ctx.Authority == ValidationContext.UtilityAuthority.Amagua
                ? _amagua.SewerMinDiameter_Lateral_mm
                : _interagua.SewerMinDiameter_Lateral_mm;

            string standard = ctx.Authority == ValidationContext.UtilityAuthority.Amagua
                ? "AMAGUA 2022" : "INTERAGUA 2019";

            if (diameter_mm < minDia)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error, "PIPE-001", "Pipe Diameter",
                    $"Pipe diameter {diameter_mm:F0} mm is less than the minimum {minDia:F0} mm.",
                    handle, standard, diameter_mm, minDia, "mm"));

            // Also check against INEN recognised sizes
            bool isInenSize = _inen.INEN3054_ValidDiameters_mm.Any(d => Math.Abs(d - diameter_mm) < 1.0);
            if (!isInenSize && diameter_mm > 0)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning, "PIPE-001W", "Pipe Diameter",
                    $"Diameter {diameter_mm:F0} mm is not a standard NTE INEN 3054 size.",
                    handle, "NTE INEN 3054", diameter_mm, null, "mm"));

            return issues;
        }

        private IEnumerable<ValidationIssue> ValidatePipeSlope(
            Pipe pipe, double slope_pct, double diameter_mm, ValidationContext ctx, string handle)
        {
            var issues = new List<ValidationIssue>();
            string standard = ctx.Authority == ValidationContext.UtilityAuthority.Amagua
                ? "AMAGUA 2022" : "INTERAGUA 2019";

            IReadOnlyDictionary<int, double> slopeTable = ctx.Authority == ValidationContext.UtilityAuthority.Amagua
                ? _amagua.SewerMinSlope_Percent
                : _interagua.SewerMinSlope_Percent;

            // Find the entry for this diameter (use next larger if exact not found)
            int diamKey = FindDiameterKey(slopeTable, (int)Math.Round(diameter_mm));
            if (diamKey > 0)
            {
                double minSlope = slopeTable[diamKey];
                if (slope_pct < minSlope)
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error, "SLOPE-001", "Pipe Slope",
                        $"Slope {slope_pct:F3}% is below minimum {minSlope:F3}% for D={diameter_mm:F0} mm pipe.",
                        handle, standard, slope_pct, minSlope, "%"));
            }

            // Maximum slope
            double maxSlope = _interagua.SewerMaxSlope_Percent;
            if (slope_pct > maxSlope)
            {
                ValidationSeverity sev = slope_pct > _interagua.SewerMaxSlopeWithEnergyDissipator_Percent
                    ? ValidationSeverity.Error
                    : ValidationSeverity.Warning;

                issues.Add(new ValidationIssue(
                    sev, "SLOPE-002", "Pipe Slope",
                    $"Slope {slope_pct:F3}% exceeds maximum {maxSlope:F1}%." +
                    (sev == ValidationSeverity.Warning ? " Energy dissipator may be required." : " Absolute limit exceeded."),
                    handle, standard, slope_pct, maxSlope, "%"));
            }

            return issues;
        }

        private IEnumerable<ValidationIssue> ValidatePipeCover(
            Pipe pipe, ValidationContext ctx, string handle)
        {
            var issues = new List<ValidationIssue>();

            // Civil 3D: pipe cover = ground elevation at pipe centre - pipe crown elevation
            double cover_m = pipe.CoverOver;

            double minCover = ctx.IsUrbanArea
                ? _interagua.SewerCoverUnderPavement_Min_m
                : _interagua.SewerCoverUnderUnpaved_Min_m;

            // Seismic zone V or VI: add buffer
            if (ctx.SeismicZone == "V" || ctx.SeismicZone == "VI")
                minCover += _nec.SeismicCoverIncrease_ZoneV_m;

            string standard = "INTERAGUA 2019 §4.5";

            if (cover_m < minCover)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error, "COVER-001", "Cover Depth",
                    $"Pipe cover {cover_m:F2} m is less than minimum {minCover:F2} m.",
                    handle, standard, cover_m, minCover, "m"));

            if (cover_m > _interagua.SewerCoverMax_m)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning, "COVER-002", "Cover Depth",
                    $"Pipe cover {cover_m:F2} m exceeds normal maximum {_interagua.SewerCoverMax_m:F2} m. Special structural design required.",
                    handle, standard, cover_m, _interagua.SewerCoverMax_m, "m"));

            return issues;
        }

        private IEnumerable<ValidationIssue> ValidateHydraulicVelocity(
            Pipe pipe, double diameter_mm, double slope_pct, ValidationContext ctx, string handle)
        {
            var issues = new List<ValidationIssue>();

            double diameter_m = diameter_mm / 1000.0;
            double slope_m_per_m = slope_pct / 100.0;
            double n = _interagua.SewerManningN_PVC;

            // Manning's equation for full-flow velocity
            if (diameter_m <= 0 || slope_m_per_m <= 0) return issues;

            double hydraulicRadius = diameter_m / 4.0;  // circular pipe full flow: R = D/4
            double velocity_ms = (1.0 / n) * Math.Pow(hydraulicRadius, 2.0 / 3.0)
                                            * Math.Pow(slope_m_per_m, 0.5);

            double minV = _interagua.SewerMinVelocity_ms;
            double maxV = _interagua.SewerMaxVelocity_ms;
            string standard = "INTERAGUA 2019 §3.4";

            if (velocity_ms < minV)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error, "VEL-001", "Hydraulic Velocity",
                    $"Full-flow velocity {velocity_ms:F3} m/s is below minimum self-cleaning velocity {minV:F2} m/s.",
                    handle, standard, velocity_ms, minV, "m/s"));

            if (velocity_ms > maxV)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error, "VEL-002", "Hydraulic Velocity",
                    $"Full-flow velocity {velocity_ms:F3} m/s exceeds maximum allowable {maxV:F1} m/s.",
                    handle, standard, velocity_ms, maxV, "m/s"));
            else if (velocity_ms > _interagua.SewerMaxVelocityAbrasive_ms)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning, "VEL-002W", "Hydraulic Velocity",
                    $"Full-flow velocity {velocity_ms:F3} m/s exceeds {_interagua.SewerMaxVelocityAbrasive_ms:F1} m/s. " +
                    "Abrasion-resistant material required (e.g. GRP or HDPE).",
                    handle, standard, velocity_ms, _interagua.SewerMaxVelocityAbrasive_ms, "m/s"));

            return issues;
        }

        private IEnumerable<ValidationIssue> ValidateSingleStructure(
            Structure structure, ValidationContext ctx)
        {
            var issues = new List<ValidationIssue>();
            string handle = structure.Handle.ToString();
            string standard = "INTERAGUA 2019 §4.3";

            // ── Sump depth ───────────────────────────────────────────────────
            double sumpDepth = structure.SumpDepth;
            if (sumpDepth < 0.10)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning, "STR-001", "Manhole Sump",
                    $"Manhole sump depth {sumpDepth:F2} m is below recommended 0.10 m.",
                    handle, standard, sumpDepth, 0.10, "m"));

            // ── Total depth ──────────────────────────────────────────────────
            double totalDepth = structure.RimElevation - structure.SumpElevation;
            if (totalDepth > _interagua.SewerCoverMax_m + 1.0)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning, "STR-002", "Manhole Depth",
                    $"Manhole total depth {totalDepth:F2} m exceeds normal limit. Deep manhole design applies.",
                    handle, standard, totalDepth, _interagua.SewerCoverMax_m, "m"));

            // ── Drop connection requirement ───────────────────────────────────
            // If the incoming pipe invert is more than 600 mm above outgoing, drop connection needed
            if (structure.ConnectedPipesCount >= 2)
            {
                double maxInletInvert  = double.MinValue;
                double outletInvert    = double.MaxValue;

                foreach (ObjectId connId in structure.GetConnectedPipes())
                {
                    using (Transaction t = structure.Database.TransactionManager.StartTransaction())
                    {
                        Pipe p = t.GetObject(connId, OpenMode.ForRead) as Pipe;
                        if (p == null) { t.Abort(); continue; }

                        // Determine if this pipe flows into or out of the structure
                        if (p.StartStructureId == structure.ObjectId)
                            outletInvert = Math.Min(outletInvert, p.StartPoint.Z);
                        else
                            maxInletInvert = Math.Max(maxInletInvert, p.EndPoint.Z);

                        t.Commit();
                    }
                }

                double inletOutletDiff = maxInletInvert - outletInvert;
                if (inletOutletDiff > _interagua.ManholeDropRequired_m)
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning, "STR-003", "Drop Connection",
                        $"Inlet invert is {inletOutletDiff:F2} m above outlet invert. " +
                        $"Outside drop connection required (> {_interagua.ManholeDropRequired_m:F2} m difference).",
                        handle, standard, inletOutletDiff, _interagua.ManholeDropRequired_m, "m"));
            }

            return issues;
        }

        private IEnumerable<ValidationIssue> ValidateNetworkSeparations(
            Network network, Transaction tr, ValidationContext ctx)
        {
            // This method checks inter-network separation when other utility networks exist.
            // Full implementation requires querying other pipe network objects in the drawing.
            var issues = new List<ValidationIssue>();

            // Placeholder: warn if no separation checks could be performed
            issues.Add(new ValidationIssue(
                ValidationSeverity.Info, "SEP-000", "Network Separation",
                "Network separation cross-check requires all utility networks to be loaded. " +
                "Minimum horizontal separation water-to-sewer = 3.00 m (INTERAGUA 2019 §5.1).",
                network.Handle.ToString(), "INTERAGUA 2019 §5.1"));

            return issues;
        }

        // ═════════════════════════════════════════════════════════════════════
        // ALIGNMENT VALIDATION METHODS (Horizontal)
        // ═════════════════════════════════════════════════════════════════════

        private IEnumerable<ValidationIssue> ValidateHorizontalCurves(
            Alignment alignment, ValidationContext ctx)
        {
            var issues = new List<ValidationIssue>();
            int V = ctx.RoadDesignSpeed_kph;
            string alignHandle = alignment.Handle.ToString();
            string standard = "NEVI-12-MTOP §3.4";

            if (!_mtop.MinCurveRadius_m.TryGetValue(V, out double minRadius))
            {
                // Interpolate for non-tabulated speed
                minRadius = InterpolateBySpeed(_mtop.MinCurveRadius_m, V);
            }

            if (!_mtop.DesirableMinCurveRadius_m.TryGetValue(V, out double desirableRadius))
                desirableRadius = InterpolateBySpeed(_mtop.DesirableMinCurveRadius_m, V);

            // Iterate alignment entities
            AlignmentEntityCollection entities = alignment.Entities;
            for (int i = 0; i < entities.Count; i++)
            {
                AlignmentEntity entity = entities[i];

                if (entity.EntityType == AlignmentEntityType.Arc)
                {
                    AlignmentArc arc = entity as AlignmentArc;
                    if (arc == null) continue;

                    double radius = arc.Radius;
                    string entHandle = $"{alignHandle}:Arc{i}";

                    if (radius < minRadius)
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error, "CURVE-001", "Horizontal Curve Radius",
                            $"Curve radius {radius:F1} m is below absolute minimum {minRadius:F1} m " +
                            $"for V={V} km/h.",
                            entHandle, standard, radius, minRadius, "m"));
                    else if (radius < desirableRadius)
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Warning, "CURVE-001W", "Horizontal Curve Radius",
                            $"Curve radius {radius:F1} m is below desirable minimum {desirableRadius:F1} m " +
                            $"for V={V} km/h. Comfort and safety may be reduced.",
                            entHandle, standard, radius, desirableRadius, "m"));

                    // Check spiral transition requirement
                    if (radius < desirableRadius)
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Warning, "CURVE-002", "Spiral Transition",
                            $"Radius {radius:F1} m < desirable minimum {desirableRadius:F1} m. " +
                            "Clothoid (Euler) transition spirals are required per NEVI-12 §3.4.3.",
                            entHandle, "NEVI-12-MTOP §3.4.3", radius, desirableRadius, "m"));
                }
            }

            return issues;
        }

        private IEnumerable<ValidationIssue> ValidateAlignmentTangents(
            Alignment alignment, ValidationContext ctx)
        {
            var issues = new List<ValidationIssue>();
            // Future: validate tangent lengths, deflection angles, sight lines
            return issues;
        }

        // ═════════════════════════════════════════════════════════════════════
        // PROFILE VALIDATION METHODS (Vertical alignment)
        // ═════════════════════════════════════════════════════════════════════

        private IEnumerable<ValidationIssue> ValidateProfileGrades(
            Profile profile, ValidationContext ctx)
        {
            var issues = new List<ValidationIssue>();
            int V = ctx.RoadDesignSpeed_kph;
            string standard = "NEVI-12-MTOP §3.5";
            string terrain   = ctx.Terrain.ToString()[0].ToString().ToUpper(); // P, O, M, E

            // Get max grade from table
            double maxGrade = 18.0;
            if (_mtop.MaxGrade_Percent.TryGetValue(V, out var terrainGrades))
            {
                if (!terrainGrades.TryGetValue(terrain, out maxGrade))
                    maxGrade = terrainGrades.Values.Max();
            }

            // Minimum grade for drainage
            double minGrade = _mtop.MinGrade_Drainage_Percent;

            ProfileEntityCollection entities = profile.Entities;
            for (int i = 0; i < entities.Count; i++)
            {
                ProfileEntity entity = entities[i];
                if (entity.EntityType == ProfileEntityType.Tangent)
                {
                    ProfileTangent tangent = entity as ProfileTangent;
                    if (tangent == null) continue;

                    double grade_pct = Math.Abs(tangent.Grade) * 100.0;
                    string entHandle = $"{profile.Handle}:Tangent{i}";

                    if (grade_pct > maxGrade)
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error, "GRADE-001", "Longitudinal Grade",
                            $"Grade {grade_pct:F2}% exceeds maximum {maxGrade:F1}% for V={V} km/h, terrain={terrain}.",
                            entHandle, standard, grade_pct, maxGrade, "%"));

                    if (grade_pct < minGrade && grade_pct > 0.001)
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Warning, "GRADE-002", "Longitudinal Grade",
                            $"Grade {grade_pct:F3}% is below minimum drainage grade {minGrade:F2}%. " +
                            "Risk of ponding and drainage problems.",
                            entHandle, standard, grade_pct, minGrade, "%"));
                }
            }

            return issues;
        }

        private IEnumerable<ValidationIssue> ValidateVerticalCurves(
            Profile profile, ValidationContext ctx)
        {
            var issues = new List<ValidationIssue>();
            int V = ctx.RoadDesignSpeed_kph;
            string standard = "NEVI-12-MTOP §3.5.3";

            _mtop.KValue_Crest.TryGetValue(V, out double kCrest);
            _mtop.KValue_Sag.TryGetValue(V, out double kSag);
            if (kCrest == 0) kCrest = InterpolateBySpeed(_mtop.KValue_Crest, V);
            if (kSag   == 0) kSag   = InterpolateBySpeed(_mtop.KValue_Sag,   V);

            ProfileEntityCollection entities = profile.Entities;
            for (int i = 0; i < entities.Count; i++)
            {
                ProfileEntity entity = entities[i];
                if (entity.EntityType != ProfileEntityType.Curve) continue;

                ProfileCurve curve = entity as ProfileCurve;
                if (curve == null) continue;

                double length_m = curve.Length;
                double gradeChange_pct = Math.Abs(curve.GradeOut - curve.GradeIn) * 100.0;
                if (gradeChange_pct < 0.001) continue;

                double kActual = length_m / gradeChange_pct;
                string entHandle = $"{profile.Handle}:VCurve{i}";

                bool isCrest = curve.GradeIn > curve.GradeOut;
                double kLimit = isCrest ? kCrest : kSag;
                string curveType = isCrest ? "Crest" : "Sag";

                if (kActual < kLimit)
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error, "VCURVE-001", $"Vertical Curve (K value – {curveType})",
                        $"{curveType} vertical curve K={kActual:F1} is below minimum K={kLimit:F1} " +
                        $"for V={V} km/h (ΔG={gradeChange_pct:F2}%, L={length_m:F1} m).",
                        entHandle, standard, kActual, kLimit, "dimensionless"));

                if (length_m < 20.0)
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning, "VCURVE-002", "Vertical Curve Length",
                        $"Vertical curve length {length_m:F1} m is below minimum 20 m.",
                        entHandle, standard, length_m, 20.0, "m"));
            }

            return issues;
        }

        // ═════════════════════════════════════════════════════════════════════
        // CROSS-SECTION VALIDATION
        // ═════════════════════════════════════════════════════════════════════

        private IEnumerable<ValidationIssue> ValidateCrossSection(
            double laneWidth_m,
            double shoulderWidth_m,
            double superelevation_percent,
            ValidationContext ctx)
        {
            var issues = new List<ValidationIssue>();
            int V = ctx.RoadDesignSpeed_kph;
            string standard = "NEVI-12-MTOP §3.3";

            // ── Lane width ───────────────────────────────────────────────────
            double minLane = GetMinLaneWidth(ctx.RoadClass);
            if (laneWidth_m < minLane)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error, "XS-001", "Lane Width",
                    $"Lane width {laneWidth_m:F2} m is below minimum {minLane:F2} m for class {ctx.RoadClass}.",
                    "CrossSection", standard, laneWidth_m, minLane, "m"));

            // ── Shoulder width ───────────────────────────────────────────────
            double minShoulder = GetMinShoulderWidth(ctx.RoadClass);
            if (shoulderWidth_m < minShoulder)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning, "XS-002", "Shoulder Width",
                    $"Shoulder width {shoulderWidth_m:F2} m is below minimum {minShoulder:F2} m for class {ctx.RoadClass}.",
                    "CrossSection", standard, shoulderWidth_m, minShoulder, "m"));

            // ── Superelevation ────────────────────────────────────────────────
            double maxSE = ctx.IsUrbanArea
                ? _mtop.MaxSuperelevation_Urban_Percent
                : _mtop.MaxSuperelevation_Percent;

            if (superelevation_percent > maxSE)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error, "XS-003", "Superelevation",
                    $"Superelevation {superelevation_percent:F2}% exceeds maximum {maxSE:F1}% per NEVI-12.",
                    "CrossSection", standard, superelevation_percent, maxSE, "%"));

            if (superelevation_percent < _mtop.NormalCrownSlope_Percent)
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Info, "XS-003I", "Superelevation",
                    $"Superelevation {superelevation_percent:F2}% is below normal crown slope {_mtop.NormalCrownSlope_Percent:F1}%. " +
                    "Ensure adequate drainage.",
                    "CrossSection", standard, superelevation_percent, _mtop.NormalCrownSlope_Percent, "%"));

            return issues;
        }

        // ═════════════════════════════════════════════════════════════════════
        // NEC SEISMIC VALIDATION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validates seismic requirements for underground infrastructure.
        /// </summary>
        public ValidationReport ValidateSeismicRequirements(
            IEnumerable<Pipe> pipes, string seismicZone, ValidationContext ctx)
        {
            var report = new ValidationReport();
            string standard = "NEC-SE-DS 2015";

            if (!_nec.SeismicZones.TryGetValue(seismicZone, out var zoneData))
            {
                report.Add(new ValidationIssue(
                    ValidationSeverity.Error, "SEIS-000", "Seismic Zone",
                    $"Unknown seismic zone '{seismicZone}'. Valid zones: I, II, III, IV, V, VI.",
                    "Global", standard));
                return report;
            }

            double pga = zoneData.PeakGroundAcceleration_g;

            foreach (Pipe pipe in pipes)
            {
                string handle = pipe.Handle.ToString();
                double cover_m = pipe.CoverOver;

                // In zones V-VI, require HDPE or equivalent for crossings
                if ((seismicZone == "V" || seismicZone == "VI") && cover_m < 1.20)
                    report.Add(new ValidationIssue(
                        ValidationSeverity.Error, "SEIS-001", "Seismic Cover",
                        $"Pipe cover {cover_m:F2} m is insufficient for seismic zone {seismicZone} (PGA={pga}g). " +
                        "Minimum 1.20 m required per NEC-SE-DS.",
                        handle, standard, cover_m, 1.20, "m"));

                // Flexible joints for seismic zones IV+
                if (string.Compare(seismicZone, "IV", StringComparison.Ordinal) >= 0)
                    report.Add(new ValidationIssue(
                        ValidationSeverity.Warning, "SEIS-002", "Seismic Joint Flexibility",
                        $"Seismic zone {seismicZone}: Flexible joints with minimum {_nec.SeismicJointFlexibility_mm} mm " +
                        "axial movement capacity required. Verify pipe material specification.",
                        handle, standard));
            }

            return report;
        }

        // ═════════════════════════════════════════════════════════════════════
        // WATER SUPPLY SPECIFIC VALIDATION
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validates a water supply pipe (pressure) against Ecuadorian norms.
        /// </summary>
        public ValidationReport ValidateWaterSupplyPipe(
            Pipe pipe, double pressure_kPa, ValidationContext ctx)
        {
            var report = new ValidationReport();
            string handle = pipe.Handle.ToString();
            double diameter_mm = pipe.InnerDiameterOrWidth * 1000.0;

            // Select authority limits
            double minDia, minPress, maxPress, minV, maxV;
            string standard;
            if (ctx.Authority == ValidationContext.UtilityAuthority.Amagua)
            {
                minDia   = _amagua.WaterMinDiameter_Residential_mm;
                minPress = _amagua.WaterMinPressure_kPa;
                maxPress = _amagua.WaterMaxPressure_kPa;
                minV     = _amagua.WaterMinVelocity_ms;
                maxV     = _amagua.WaterMaxVelocity_ms;
                standard = "AMAGUA 2022";
            }
            else
            {
                minDia   = _interagua.WaterMinDiameter_Residential_mm;
                minPress = _interagua.WaterMinPressure_kPa;
                maxPress = _interagua.WaterMaxPressure_kPa;
                minV     = _interagua.WaterMinVelocity_ms;
                maxV     = _interagua.WaterMaxVelocity_ms;
                standard = "INTERAGUA 2019";
            }

            // Diameter check
            if (diameter_mm < minDia)
                report.Add(new ValidationIssue(
                    ValidationSeverity.Error, "WPIPE-001", "Water Pipe Diameter",
                    $"Diameter {diameter_mm:F0} mm < minimum {minDia:F0} mm for water supply.",
                    handle, standard, diameter_mm, minDia, "mm"));

            // Pressure checks
            if (pressure_kPa < minPress)
                report.Add(new ValidationIssue(
                    ValidationSeverity.Error, "WPIPE-002", "Water Pressure",
                    $"Pressure {pressure_kPa:F0} kPa < minimum service pressure {minPress:F0} kPa.",
                    handle, standard, pressure_kPa, minPress, "kPa"));

            if (pressure_kPa > maxPress)
                report.Add(new ValidationIssue(
                    ValidationSeverity.Error, "WPIPE-003", "Water Pressure",
                    $"Pressure {pressure_kPa:F0} kPa > maximum allowable {maxPress:F0} kPa. " +
                    "Pressure reducing valve required.",
                    handle, standard, pressure_kPa, maxPress, "kPa"));

            // Velocity from Hazen-Williams (approximate, requires flow data)
            // Full implementation requires hydraulic simulation results
            report.Add(new ValidationIssue(
                ValidationSeverity.Info, "WPIPE-004", "Water Velocity",
                $"Velocity validation requires hydraulic model results. " +
                $"Limits: min={minV:F2} m/s, max={maxV:F2} m/s per {standard}.",
                handle, standard));

            return report;
        }

        // ═════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ═════════════════════════════════════════════════════════════════════

        private static int FindDiameterKey(IReadOnlyDictionary<int, double> table, int diameter_mm)
        {
            if (table.ContainsKey(diameter_mm))
                return diameter_mm;

            // Round up to next available diameter
            foreach (int key in table.Keys.OrderBy(k => k))
            {
                if (key >= diameter_mm)
                    return key;
            }
            return -1;
        }

        private static double InterpolateBySpeed(IReadOnlyDictionary<int, double> table, int speed)
        {
            var sorted = table.OrderBy(kv => kv.Key).ToList();
            if (!sorted.Any()) return 0;
            if (speed <= sorted.First().Key) return sorted.First().Value;
            if (speed >= sorted.Last().Key)  return sorted.Last().Value;

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (speed >= sorted[i].Key && speed <= sorted[i + 1].Key)
                {
                    double t = (double)(speed - sorted[i].Key) /
                               (sorted[i + 1].Key - sorted[i].Key);
                    return sorted[i].Value + t * (sorted[i + 1].Value - sorted[i].Value);
                }
            }
            return sorted.Last().Value;
        }

        private double GetMinLaneWidth(string roadClass)
        {
            switch (roadClass?.ToUpperInvariant())
            {
                case "E":   return _mtop.LaneWidth_E_m;
                case "R1":  return _mtop.LaneWidth_R1_m;
                case "R2":  return _mtop.LaneWidth_R2_m;
                case "R3":  return _mtop.LaneWidth_R3_m;
                case "U_A": return _mtop.LaneWidth_Urban_A_m;
                case "U_C": return _mtop.LaneWidth_Urban_C_m;
                case "U_L": return _mtop.LaneWidth_Urban_L_m;
                default:    return _mtop.LaneWidth_Min_m;
            }
        }

        private double GetMinShoulderWidth(string roadClass)
        {
            switch (roadClass?.ToUpperInvariant())
            {
                case "E":   return _mtop.ShoulderWidth_E_Paved_m;
                case "R1":  return _mtop.ShoulderWidth_R1_Paved_m;
                case "R2":  return _mtop.ShoulderWidth_R2_Paved_m;
                case "R3":  return _mtop.ShoulderWidth_R3_Unpaved_m;
                default:    return 0.50;
            }
        }
    }
}
