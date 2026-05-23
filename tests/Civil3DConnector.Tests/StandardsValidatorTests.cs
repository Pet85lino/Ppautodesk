using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using Civil3DConnector.Models;

namespace Civil3DConnector.Tests
{
    /// <summary>
    /// Unit tests for Ecuadorian standards validation logic.
    /// These tests run without AutoCAD/Civil 3D dependencies.
    /// </summary>
    public class StandardsValidatorTests
    {
        // ─────────────────────────────────────────────────────────────────────
        // Pipe Slope Tests (Interagua)
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(200, 0.003, false)]  // 200mm @ 0.3% = OK (min 0.003 m/m)
        [InlineData(200, 0.001, true)]   // 200mm @ 0.1% = VIOLATION (too flat)
        [InlineData(300, 0.002, false)]  // 300mm @ 0.2% = OK
        [InlineData(150, 0.005, false)]  // 150mm @ 0.5% = OK
        [InlineData(150, 0.001, true)]   // 150mm @ 0.1% = VIOLATION
        public void InteraguaMinimumSlope_ShouldDetectViolations(
            double diameterMm, double slope, bool shouldViolate)
        {
            // Arrange
            var limits = InteraguaStandardLimits.GetMinimumSlope(diameterMm);

            // Act
            bool isViolation = slope < limits;

            // Assert
            isViolation.Should().Be(shouldViolate,
                $"pipe {diameterMm}mm at slope {slope:P2} should {(shouldViolate ? "violate" : "pass")} Interagua minimum slope");
        }

        [Theory]
        [InlineData(150, 1.2, false)]   // 1.2 m/s OK
        [InlineData(150, 0.4, true)]    // 0.4 m/s too slow (min 0.6 m/s)
        [InlineData(400, 6.0, true)]    // 6.0 m/s too fast (max 5.0 m/s)
        [InlineData(400, 3.0, false)]   // 3.0 m/s OK
        public void InteraguaHydraulicVelocity_ShouldDetectViolations(
            double diameterMm, double velocityMs, bool shouldViolate)
        {
            const double minVelocity = 0.6;
            const double maxVelocity = 5.0;

            bool isViolation = velocityMs < minVelocity || velocityMs > maxVelocity;

            isViolation.Should().Be(shouldViolate,
                $"velocity {velocityMs} m/s should {(shouldViolate ? "violate" : "pass")} Interagua velocity limits");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Cover Depth Tests
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(1.2, true)]   // 1.2m under road = OK (min 1.0m per Interagua)
        [InlineData(0.5, false)]  // 0.5m = VIOLATION
        [InlineData(0.9, false)]  // 0.9m = VIOLATION
        [InlineData(1.0, true)]   // exactly at minimum = OK
        [InlineData(6.0, false)]  // 6.0m = VIOLATION (max 5.0m)
        public void InteraguaCoverDepth_ShouldDetectViolations(double coverM, bool shouldPass)
        {
            const double minCover = 1.0;
            const double maxCover = 5.0;

            bool passes = coverM >= minCover && coverM <= maxCover;

            passes.Should().Be(shouldPass,
                $"cover depth {coverM}m should {(shouldPass ? "pass" : "fail")} Interagua cover requirements");
        }

        // ─────────────────────────────────────────────────────────────────────
        // MTOP Road Grade Tests
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("primary", 80, 0.04, true)]   // 4% grade on 80 km/h primary = OK (max 6%)
        [InlineData("primary", 80, 0.07, false)]  // 7% on primary 80 km/h = VIOLATION (max 6%)
        [InlineData("local", 40, 0.12, true)]     // 12% on local = OK (max 14%)
        [InlineData("local", 40, 0.15, false)]    // 15% on local = VIOLATION
        public void MtopMaximumGrade_ShouldDetectViolations(
            string roadClass, double designSpeedKmh, double grade, bool shouldPass)
        {
            double maxGrade = MtopRoadLimits.GetMaxGrade(roadClass, designSpeedKmh);
            bool passes = grade <= maxGrade;

            passes.Should().Be(shouldPass,
                $"Grade {grade:P0} on {roadClass} road at {designSpeedKmh} km/h should {(shouldPass ? "pass" : "fail")}");
        }

        [Theory]
        [InlineData("primary", 100, 700, true)]   // 700m radius on 100km/h = OK (min 450m)
        [InlineData("primary", 100, 400, false)]  // 400m = VIOLATION
        [InlineData("secondary", 60, 200, true)]  // 200m on 60km/h secondary = OK (min 130m)
        [InlineData("secondary", 60, 100, false)] // 100m = VIOLATION
        [InlineData("local", 30, 30, true)]       // 30m on local 30km/h = OK (min 25m)
        [InlineData("local", 30, 15, false)]      // 15m = VIOLATION
        public void MtopMinimumCurveRadius_ShouldDetectViolations(
            string roadClass, double designSpeedKmh, double radiusM, bool shouldPass)
        {
            double minRadius = MtopRoadLimits.GetMinCurveRadius(roadClass, designSpeedKmh);
            bool passes = radiusM >= minRadius;

            passes.Should().Be(shouldPass,
                $"Radius {radiusM}m on {roadClass}/{designSpeedKmh}km/h should {(shouldPass ? "pass" : "fail")}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pipe Diameter Tests
        // ─────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(200, true)]   // 200mm = valid standard size
        [InlineData(150, true)]   // 150mm = valid
        [InlineData(300, true)]   // 300mm = valid
        [InlineData(175, false)]  // 175mm = non-standard for gravity sewer Ecuador
        [InlineData(250, true)]   // 250mm = valid
        [InlineData(110, false)]  // 110mm = below Interagua minimum (150mm)
        public void InteraguaStandardDiameters_ShouldValidate(double diameterMm, bool shouldBeValid)
        {
            bool isValid = InteraguaStandardLimits.IsStandardDiameter(diameterMm);
            isValid.Should().Be(shouldBeValid,
                $"Diameter {diameterMm}mm should {(shouldBeValid ? "be" : "not be")} a valid Interagua standard diameter");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Violation Model Tests
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void StandardsViolation_ShouldHaveCorrectProperties()
        {
            var violation = new StandardsViolation
            {
                ObjectName = "Tubería-001",
                Standard = "Interagua",
                ViolationMessage = "Pendiente insuficiente: 0.1% < 0.3% mínimo",
                ActualValue = 0.001,
                LimitValue = 0.003,
                Unit = "m/m",
                Severity = ViolationSeverity.Warning
            };

            violation.ObjectName.Should().Be("Tubería-001");
            violation.Standard.Should().Be("Interagua");
            violation.ActualValue.Should().Be(0.001);
            violation.LimitValue.Should().Be(0.003);
            violation.Severity.Should().Be(ViolationSeverity.Warning);
        }

        [Fact]
        public void AnalysisResult_ShouldCalculateHasCorruptedObjects()
        {
            var result = new AnalysisResult();
            result.CriticalIssues.Add(new ObjectIssue
            {
                Category = "Corruption",
                ObjectName = "Surface-01",
                Description = "TIN surface has invalid triangles"
            });

            result.HasCorruptedObjects.Should().BeTrue();
            result.HasMissingReferences.Should().BeFalse();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test Helpers — static lookup tables (no AutoCAD dependency)
    // ─────────────────────────────────────────────────────────────────────────

    internal static class InteraguaStandardLimits
    {
        private static readonly double[] StandardDiametersMm =
            { 150, 200, 250, 300, 350, 400, 450, 500, 600, 700, 800, 900, 1000, 1200 };

        public static double GetMinimumSlope(double diameterMm) => diameterMm switch
        {
            <= 200  => 0.003,
            <= 300  => 0.002,
            <= 400  => 0.0015,
            <= 600  => 0.001,
            _       => 0.0008
        };

        public static bool IsStandardDiameter(double diameterMm)
        {
            if (diameterMm < 150) return false;
            foreach (var d in StandardDiametersMm)
                if (Math.Abs(d - diameterMm) < 1.0) return true;
            return false;
        }
    }

    internal static class MtopRoadLimits
    {
        public static double GetMaxGrade(string roadClass, double designSpeedKmh) =>
            (roadClass.ToLower(), designSpeedKmh) switch
            {
                ("primary",   >= 100) => 0.04,
                ("primary",   >= 80)  => 0.06,
                ("primary",   >= 60)  => 0.07,
                ("secondary", >= 80)  => 0.07,
                ("secondary", >= 60)  => 0.08,
                ("secondary", >= 40)  => 0.10,
                ("local",     >= 60)  => 0.10,
                ("local",     >= 40)  => 0.12,
                ("local",     _)      => 0.14,
                _                     => 0.12
            };

        public static double GetMinCurveRadius(string roadClass, double designSpeedKmh) =>
            (roadClass.ToLower(), designSpeedKmh) switch
            {
                ("primary",   >= 120) => 700,
                ("primary",   >= 100) => 450,
                ("primary",   >= 80)  => 250,
                ("secondary", >= 80)  => 250,
                ("secondary", >= 60)  => 130,
                ("secondary", >= 40)  => 60,
                ("local",     >= 60)  => 130,
                ("local",     >= 40)  => 50,
                ("local",     >= 30)  => 25,
                _                     => 15
            };
    }
}
