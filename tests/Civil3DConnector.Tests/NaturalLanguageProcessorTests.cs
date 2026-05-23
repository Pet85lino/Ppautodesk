using FluentAssertions;
using Xunit;
using Civil3DConnector.AI;
using Civil3DConnector.Models;

namespace Civil3DConnector.Tests
{
    /// <summary>Tests for the NLP command parser (no AutoCAD dependency).</summary>
    public class NaturalLanguageProcessorTests
    {
        private readonly NaturalLanguageProcessor _nlp = new();

        [Theory]
        [InlineData("crear corredor de 500m", IntentType.CreateCorridor)]
        [InlineData("create corridor from alignment CL-1", IntentType.CreateCorridor)]
        [InlineData("generar red sanitaria", IntentType.CreatePipeNetwork)]
        [InlineData("generate sewer pipe network", IntentType.CreatePipeNetwork)]
        [InlineData("analizar dwg", IntentType.AnalyzeDwg)]
        [InlineData("analyze drawing for errors", IntentType.AnalyzeDwg)]
        [InlineData("validar pendientes interagua", IntentType.ValidateStandards)]
        [InlineData("validate against interagua standards", IntentType.ValidateStandards)]
        [InlineData("exportar a geojson", IntentType.ExportGis)]
        [InlineData("calcular volumen de corte y relleno", IntentType.CalculateVolume)]
        [InlineData("generar script dynamo", IntentType.GenerateDynamo)]
        [InlineData("exportar landxml", IntentType.ExportLandXml)]
        public void Parse_ShouldIdentifyIntentCorrectly(string command, IntentType expectedIntent)
        {
            var plan = _nlp.Parse(command);

            plan.Should().NotBeNull();
            plan.Intent.Should().Be(expectedIntent,
                $"command '{command}' should map to intent {expectedIntent}");
            plan.Confidence.Should().BeGreaterThan(0.5,
                $"confidence for '{command}' should be at least 50%");
        }

        [Fact]
        public void Parse_UnrecognizedCommand_ShouldReturnLowConfidence()
        {
            var plan = _nlp.Parse("haz algo con el dibujo qwerty1234");

            plan.Intent.Should().Be(IntentType.Unknown);
            plan.Confidence.Should().BeLessThan(0.3);
        }

        [Fact]
        public void Parse_CorridorCommand_ShouldIncludeWorkflowSteps()
        {
            var plan = _nlp.Parse("crear corredor en alineamiento Carretera-Principal perfil PF-001 ensamble Sección-Típica");

            plan.Intent.Should().Be(IntentType.CreateCorridor);
            plan.Steps.Should().NotBeEmpty();
            plan.Steps.Should().HaveCountGreaterThan(1);
            plan.RequiredApis.Should().Contain(api => api.Contains("Corridor") || api.Contains("corridor"));
        }

        [Fact]
        public void Parse_ValidateCommand_ShouldExtractStandard()
        {
            var plan = _nlp.Parse("validar normas mtop para pendientes de carretera");

            plan.Intent.Should().Be(IntentType.ValidateStandards);
            plan.Parameters.Should().ContainKey("standard");
            plan.Parameters["standard"].ToString().Should().ContainEquivalentOf("MTOP");
        }

        [Fact]
        public void Parse_ShouldBeThreadSafe()
        {
            var commands = new[]
            {
                "crear corredor",
                "analizar dwg",
                "validar interagua",
                "generar dynamo",
                "exportar geojson"
            };

            var plans = new ExecutionPlan[commands.Length];
            var tasks = new System.Threading.Tasks.Task[commands.Length];

            for (int i = 0; i < commands.Length; i++)
            {
                int idx = i;
                tasks[idx] = System.Threading.Tasks.Task.Run(() =>
                    plans[idx] = _nlp.Parse(commands[idx]));
            }

            System.Threading.Tasks.Task.WaitAll(tasks);

            foreach (var plan in plans)
                plan.Should().NotBeNull();
        }
    }
}
