using System;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using Civil3DConnector.Generators;

namespace Civil3DConnector.Tests
{
    /// <summary>Tests that the Dynamo JSON generator produces valid .dyn files.</summary>
    public class DynamoScriptGeneratorTests
    {
        private readonly DynamoScriptGenerator _generator = new();

        [Fact]
        public void GenerateCorridorScript_ShouldProduceValidDynJson()
        {
            var json = _generator.GenerateCorridorScript();

            json.Should().NotBeNullOrWhiteSpace();
            var obj = JObject.Parse(json);    // Throws if invalid JSON

            obj.Should().ContainKey("Uuid");
            obj.Should().ContainKey("IsCustomNode");
            obj.Should().ContainKey("Nodes");
            obj.Should().ContainKey("Connectors");
            obj["IsCustomNode"]!.Value<bool>().Should().BeFalse();
        }

        [Fact]
        public void GeneratePipeNetworkScript_ShouldHaveInputNodes()
        {
            var json = _generator.GeneratePipeNetworkScript();
            var obj = JObject.Parse(json);

            var nodes = obj["Nodes"] as JArray;
            nodes.Should().NotBeNull();
            nodes!.Count.Should().BeGreaterThan(3);
        }

        [Theory]
        [InlineData("Corredor")]
        [InlineData("RedTuberias")]
        [InlineData("Superficie")]
        [InlineData("Alineamiento")]
        [InlineData("Perfil")]
        public void AllWorkflowTemplates_ShouldProduceValidJson(string workflow)
        {
            Func<string> generate = workflow switch
            {
                "Corredor"     => _generator.GenerateCorridorScript,
                "RedTuberias"  => _generator.GeneratePipeNetworkScript,
                "Superficie"   => _generator.GenerateSurfaceScript,
                "Alineamiento" => _generator.GenerateAlignmentScript,
                "Perfil"       => _generator.GenerateProfileScript,
                _              => throw new ArgumentException($"Unknown workflow: {workflow}")
            };

            var json = generate();
            json.Should().NotBeNullOrWhiteSpace();

            var act = () => JObject.Parse(json);
            act.Should().NotThrow($"workflow '{workflow}' should produce valid JSON");
        }

        [Fact]
        public void GeneratedScript_ShouldContainDynamoVersion()
        {
            var json = _generator.GenerateCorridorScript();
            var obj = JObject.Parse(json);

            obj.Should().ContainKey("DynamoVersion");
            var version = obj["DynamoVersion"]!.ToString();
            version.Should().MatchRegex(@"^\d+\.\d+");
        }

        [Fact]
        public void GenerateCustomScript_ShouldReturnFallback_ForEmptyDescription()
        {
            var json = _generator.GenerateCustomScript("");
            json.Should().NotBeNullOrWhiteSpace();
            var act = () => JObject.Parse(json);
            act.Should().NotThrow();
        }
    }
}
