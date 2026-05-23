// DynamoScriptGenerator.cs
// Programmatically builds Dynamo 2.x/3.x (.dyn) JSON graph files for Civil 3D.
//
// Key design decisions:
//  - The graph object model (DynamoGraph, DynNode, DynConnector, etc.) maps
//    1-to-1 with the JSON schema that Dynamo's WorkspaceModel deserialises.
//  - System.Text.Json is used for serialisation (available .NET 5+).  A
//    Newtonsoft.Json shim is included for .NET Framework targets that ship
//    with AutoCAD/Civil 3D.
//  - All public factory methods return a DynamoGraph that can be further
//    customised before calling ToJson() / SaveToFile().
//  - GUIDs are generated fresh per call so multiple graphs from the same
//    template never share identifiers.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Civil3DConnector.Templates;

namespace Civil3DConnector.Generators
{
    // ======================================================================
    //  Public object model
    // ======================================================================

    /// <summary>Dynamo node port (input or output).</summary>
    public sealed class DynPort
    {
        [JsonPropertyName("Id")]              public string Id              { get; set; } = Guid.NewGuid().ToString();
        [JsonPropertyName("Name")]            public string Name            { get; set; } = string.Empty;
        [JsonPropertyName("Description")]     public string Description     { get; set; } = string.Empty;
        [JsonPropertyName("UsingDefaultValue")] public bool UsingDefaultValue { get; set; } = false;
        [JsonPropertyName("Level")]           public int Level              { get; set; } = 2;
        [JsonPropertyName("UseLevels")]       public bool UseLevels         { get; set; } = false;
        [JsonPropertyName("KeepListStructure")] public bool KeepListStructure { get; set; } = false;
    }

    /// <summary>A single node in the Dynamo graph.</summary>
    public sealed class DynNode
    {
        // Discriminator understood by Dynamo's JsonConverter
        [JsonPropertyName("ConcreteType")]    public string ConcreteType    { get; set; } = "Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore";
        [JsonPropertyName("NodeType")]        public string NodeType        { get; set; } = "FunctionNode";
        [JsonPropertyName("FunctionSignature")] public string? FunctionSignature { get; set; }
        [JsonPropertyName("Engine")]          public string? Engine         { get; set; }   // Python only
        [JsonPropertyName("EngineName")]      public string? EngineName     { get; set; }   // Python only
        [JsonPropertyName("Code")]            public string? Code           { get; set; }   // CodeBlock / Python
        [JsonPropertyName("VariableInputPorts")] public bool? VariableInputPorts { get; set; } // Python only
        [JsonPropertyName("Id")]              public string Id              { get; set; } = Guid.NewGuid().ToString();
        [JsonPropertyName("Name")]            public string Name            { get; set; } = string.Empty;
        [JsonPropertyName("Description")]     public string Description     { get; set; } = string.Empty;
        [JsonPropertyName("Replication")]     public string Replication     { get; set; } = "Auto";
        [JsonPropertyName("X")]               public double X               { get; set; }
        [JsonPropertyName("Y")]               public double Y               { get; set; }
        [JsonPropertyName("Inputs")]          public List<DynPort> Inputs   { get; set; } = new();
        [JsonPropertyName("Outputs")]         public List<DynPort> Outputs  { get; set; } = new();
    }

    /// <summary>Wire connecting an output port to an input port.</summary>
    public sealed class DynConnector
    {
        [JsonPropertyName("Start")] public string Start { get; set; } = string.Empty;
        [JsonPropertyName("End")]   public string End   { get; set; } = string.Empty;
        [JsonPropertyName("Id")]    public string Id    { get; set; } = Guid.NewGuid().ToString();
    }

    /// <summary>Package dependency entry.</summary>
    public sealed class DynLibraryDependency
    {
        [JsonPropertyName("Name")]          public string Name          { get; set; } = string.Empty;
        [JsonPropertyName("Version")]       public string Version       { get; set; } = "1.0.0";
        [JsonPropertyName("ReferenceType")] public string ReferenceType { get; set; } = "Package";
        [JsonPropertyName("Nodes")]         public List<string> Nodes   { get; set; } = new();
    }

    /// <summary>Per-node view state.</summary>
    public sealed class DynNodeView
    {
        [JsonPropertyName("ShowGeometry")]  public bool   ShowGeometry  { get; set; } = true;
        [JsonPropertyName("Name")]          public string Name          { get; set; } = string.Empty;
        [JsonPropertyName("Id")]            public string Id            { get; set; } = string.Empty;
        [JsonPropertyName("IsSetAsInput")]  public bool   IsSetAsInput  { get; set; } = false;
        [JsonPropertyName("IsSetAsOutput")] public bool   IsSetAsOutput { get; set; } = false;
        [JsonPropertyName("Excluded")]      public bool   Excluded      { get; set; } = false;
        [JsonPropertyName("X")]             public double X             { get; set; }
        [JsonPropertyName("Y")]             public double Y             { get; set; }
    }

    /// <summary>Top-level Dynamo graph document.</summary>
    public sealed class DynamoGraph
    {
        [JsonPropertyName("Uuid")]            public string Uuid            { get; set; } = Guid.NewGuid().ToString();
        [JsonPropertyName("IsCustomNode")]    public bool   IsCustomNode    { get; set; } = false;
        [JsonPropertyName("Description")]     public string Description     { get; set; } = string.Empty;
        [JsonPropertyName("Name")]            public string Name            { get; set; } = "Untitled";
        [JsonPropertyName("ElementResolver")] public object ElementResolver { get; set; } = new { ResolutionMap = new { } };
        [JsonPropertyName("Inputs")]          public List<object> Inputs    { get; set; } = new();
        [JsonPropertyName("Outputs")]         public List<object> Outputs   { get; set; } = new();
        [JsonPropertyName("Nodes")]           public List<DynNode> Nodes    { get; set; } = new();
        [JsonPropertyName("Connectors")]      public List<DynConnector> Connectors { get; set; } = new();
        [JsonPropertyName("Dependencies")]    public List<object> Dependencies     { get; set; } = new();
        [JsonPropertyName("NodeLibraryDependencies")] public List<DynLibraryDependency> NodeLibraryDependencies { get; set; } = new();
        [JsonPropertyName("Thumbnail")]       public string Thumbnail       { get; set; } = string.Empty;
        [JsonPropertyName("GraphDocumentationURL")] public string? GraphDocumentationURL { get; set; } = null;
        [JsonPropertyName("ExtensionWorkspaceData")] public List<object> ExtensionWorkspaceData { get; set; } = new();
        [JsonPropertyName("Author")]          public string Author          { get; set; } = "Civil3DConnector";
        [JsonPropertyName("Linting")]         public object Linting         { get; set; } = new
        {
            activeLinter   = "None",
            activeLinterId = "7b75fb44-43fd-4631-a878-29f4d5d8399a",
            warningCount   = 0,
            errorCount     = 0
        };
        [JsonPropertyName("Bindings")]        public List<object> Bindings  { get; set; } = new();
        [JsonPropertyName("View")]            public DynView View           { get; set; } = new();

        // ------------------------------------------------------------------ //
        //  Serialisation helpers
        // ------------------------------------------------------------------ //

        private static readonly JsonSerializerOptions _serOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Serialise the graph to a JSON string (.dyn content).</summary>
        public string ToJson() => JsonSerializer.Serialize(this, _serOpts);

        /// <summary>Write the graph JSON to <paramref name="filePath"/>.</summary>
        public void SaveToFile(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir!);
            File.WriteAllText(filePath, ToJson(), Encoding.UTF8);
        }

        // ------------------------------------------------------------------ //
        //  Convenience graph-building helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Auto-populate the <see cref="View"/> NodeViews collection from
        /// the current Nodes list so callers don't have to do it manually.
        /// </summary>
        public DynamoGraph SyncNodeViews()
        {
            View.NodeViews = Nodes.Select(n => new DynNodeView
            {
                Id   = n.Id,
                Name = n.Name,
                X    = n.X,
                Y    = n.Y,
            }).ToList();
            return this;
        }

        /// <summary>
        /// Connect output port <paramref name="fromPortId"/> to input port
        /// <paramref name="toPortId"/> and add the wire to Connectors.
        /// </summary>
        public DynamoGraph Connect(string fromPortId, string toPortId)
        {
            Connectors.Add(new DynConnector { Start = fromPortId, End = toPortId });
            return this;
        }
    }

    /// <summary>View section of a Dynamo graph document.</summary>
    public sealed class DynView
    {
        [JsonPropertyName("Dynamo")]       public object DynamoSection { get; set; } = new
        {
            ScaleFactor              = 1.0,
            HasRunWithoutCrash       = false,
            IsVisibleInDynamoLibrary = true,
            Version                  = "2.18.0.3593",
            RunType                  = "Manual",
            RunPeriod                = "1000"
        };
        [JsonPropertyName("Camera")]       public object Camera { get; set; } = new
        {
            Name  = "Background Preview",
            EyeX  = -17.0, EyeY = 24.0, EyeZ = 50.0,
            LookX = 12.0, LookY = -13.0, LookZ = -58.0,
            UpX   = 0.0, UpY = 1.0, UpZ = 0.0
        };
        [JsonPropertyName("ConnectorPins")] public List<object> ConnectorPins { get; set; } = new();
        [JsonPropertyName("NodeViews")]     public List<DynNodeView> NodeViews { get; set; } = new();
        [JsonPropertyName("Annotations")]   public List<object> Annotations   { get; set; } = new();
        [JsonPropertyName("Notes")]         public List<object> Notes         { get; set; } = new();
        [JsonPropertyName("Groups")]        public List<object> Groups        { get; set; } = new();
    }

    // ======================================================================
    //  Node factory helpers  (static inner class keeps the API surface tidy)
    // ======================================================================

    /// <summary>
    /// Factory methods for common Dynamo node types.
    /// </summary>
    public static class NodeFactory
    {
        // ---- Zero-touch (C# API) node ----
        public static DynNode ZeroTouch(
            string functionSignature,
            string name,
            string description,
            double x, double y,
            IEnumerable<(string id, string name, string desc)>? inputs  = null,
            IEnumerable<(string id, string name, string desc)>? outputs = null)
        {
            var n = new DynNode
            {
                ConcreteType      = "Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore",
                NodeType          = "FunctionNode",
                FunctionSignature = functionSignature,
                Name              = name,
                Description       = description,
                Replication       = "Auto",
                X = x, Y = y,
            };
            if (inputs  != null) n.Inputs  = inputs .Select(t => new DynPort { Id = t.id, Name = t.name, Description = t.desc }).ToList();
            if (outputs != null) n.Outputs = outputs.Select(t => new DynPort { Id = t.id, Name = t.name, Description = t.desc }).ToList();
            return n;
        }

        // ---- Code Block node (DesignScript) ----
        public static DynNode CodeBlock(
            string code,
            double x, double y,
            IEnumerable<(string id, string name, string desc)>? inputs  = null,
            IEnumerable<(string id, string name, string desc)>? outputs = null)
        {
            var n = new DynNode
            {
                ConcreteType = "Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore",
                NodeType     = "CodeBlockNode",
                Name         = "Code Block",
                Description  = "DesignScript expression",
                Code         = code,
                Replication  = "Disabled",
                X = x, Y = y,
            };
            if (inputs  != null) n.Inputs  = inputs .Select(t => new DynPort { Id = t.id, Name = t.name, Description = t.desc }).ToList();
            if (outputs != null) n.Outputs = outputs.Select(t => new DynPort { Id = t.id, Name = t.name, Description = t.desc }).ToList();
            return n;
        }

        // ---- CPython 3 script node ----
        public static DynNode PythonScript(
            string code,
            double x, double y,
            string name = "Python Script",
            string description = "Python 3 (CPython) script",
            IEnumerable<(string id, string name, string desc)>? inputs  = null,
            IEnumerable<(string id, string name, string desc)>? outputs = null)
        {
            var n = new DynNode
            {
                ConcreteType        = "Dynamo.Graph.Nodes.PythonNode, DSCPython",
                NodeType            = "PythonScriptNode",
                Engine              = "CPython3",
                EngineName          = "CPython3",
                VariableInputPorts  = true,
                Name                = name,
                Description         = description,
                Code                = code,
                Replication         = "Disabled",
                X = x, Y = y,
            };
            if (inputs  != null) n.Inputs  = inputs .Select(t => new DynPort { Id = t.id, Name = t.name, Description = t.desc }).ToList();
            if (outputs != null) n.Outputs = outputs.Select(t => new DynPort { Id = t.id, Name = t.name, Description = t.desc }).ToList();
            return n;
        }

        // ---- Input node (String / Number / Boolean) ----
        public static DynNode StringInput(string value, string name, double x, double y)
        {
            var outPortId = Guid.NewGuid().ToString();
            return new DynNode
            {
                ConcreteType = "Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore",
                NodeType     = "FunctionNode",
                FunctionSignature = "DSCore.String.Concat@string[],string",
                Name         = name,
                Description  = $"String input: {value}",
                Code         = $"\"{value}\"",
                Replication  = "Disabled",
                X = x, Y = y,
                Outputs      = new List<DynPort> { new DynPort { Id = outPortId, Name = ">>", Description = value } }
            };
        }

        public static DynNode NumberInput(double value, string name, double x, double y)
        {
            var outPortId = Guid.NewGuid().ToString();
            return new DynNode
            {
                ConcreteType = "Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore",
                NodeType     = "CodeBlockNode",
                Name         = name,
                Description  = $"Numeric input: {value}",
                Code         = value.ToString("G"),
                Replication  = "Disabled",
                X = x, Y = y,
                Outputs      = new List<DynPort> { new DynPort { Id = outPortId, Name = ">>", Description = value.ToString("G") } }
            };
        }

        // ---- Civil 3D specific nodes ----
        public static DynNode GetAlignments(double x, double y) => ZeroTouch(
            functionSignature: "Civil3D.Alignment.GetAlignments",
            name:        "Civil3D.Alignment.GetAlignments",
            description: "Get all alignments in the active document",
            x: x, y: y,
            outputs: new[] { (Guid.NewGuid().ToString(), "alignments", "All Alignment objects") });

        public static DynNode GetSurfaces(double x, double y) => ZeroTouch(
            functionSignature: "Civil3D.Surface.GetSurfaces",
            name:        "Civil3D.Surface.GetSurfaces",
            description: "Get all TIN surfaces in the active document",
            x: x, y: y,
            outputs: new[] { (Guid.NewGuid().ToString(), "surfaces", "All TIN Surface objects") });

        public static DynNode GetCorridors(double x, double y) => ZeroTouch(
            functionSignature: "Civil3D.Corridor.GetCorridors",
            name:        "Civil3D.Corridor.GetCorridors",
            description: "Get all corridors in the active document",
            x: x, y: y,
            outputs: new[] { (Guid.NewGuid().ToString(), "corridors", "All Corridor objects") });

        public static DynNode GetPipeNetworks(double x, double y) => ZeroTouch(
            functionSignature: "Civil3D.PipeNetwork.GetNetworks",
            name:        "Civil3D.PipeNetwork.GetNetworks",
            description: "Get all pipe networks in the active document",
            x: x, y: y,
            outputs: new[] { (Guid.NewGuid().ToString(), "networks", "All PipeNetwork objects") });

        public static DynNode CreateCorridor(double x, double y)
        {
            var inAlign   = Guid.NewGuid().ToString();
            var inProfile = Guid.NewGuid().ToString();
            var inAssem   = Guid.NewGuid().ToString();
            var inName    = Guid.NewGuid().ToString();
            var outCorr   = Guid.NewGuid().ToString();
            return ZeroTouch(
                functionSignature: "Civil3D.Corridor.ByAlignmentProfileAssembly@Civil3D.Alignment,Civil3D.Profile,Civil3D.Assembly,string",
                name:        "Civil3D.Corridor.ByAlignmentProfileAssembly",
                description: "Create a corridor",
                x: x, y: y,
                inputs:  new[] { (inAlign, "alignment", "Baseline alignment"), (inProfile, "profile", "Baseline profile"), (inAssem, "assembly", "Assembly"), (inName, "name", "Corridor name") },
                outputs: new[] { (outCorr, "corridor", "Created corridor") });
        }
    }

    // ======================================================================
    //  Main generator class
    // ======================================================================

    /// <summary>
    /// Generates complete Dynamo (.dyn) graph files for Civil 3D workflows.
    /// </summary>
    public sealed class DynamoScriptGenerator
    {
        // ------------------------------------------------------------------ //
        //  Template-based factory methods
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Returns a fresh DynamoGraph built from the CORRIDOR_CREATION_TEMPLATE,
        /// with all node GUIDs replaced so the graph is unique.
        /// </summary>
        public DynamoGraph CreateCorridorGraph(
            string graphName        = "Corridor Creation",
            string description      = "Create a Civil 3D corridor from an alignment, profile, and assembly.")
        {
            var json  = ReplaceNodeGuids(DynamoTemplates.CORRIDOR_CREATION_TEMPLATE);
            var graph = ParseTemplateJson(json);
            graph.Name        = graphName;
            graph.Description = description;
            graph.Uuid        = Guid.NewGuid().ToString();
            return graph;
        }

        /// <summary>
        /// Returns a fresh DynamoGraph built from the PIPE_NETWORK_TEMPLATE.
        /// </summary>
        public DynamoGraph CreatePipeNetworkGraph(
            string graphName   = "Pipe Network Creation",
            string description = "Create a pipe network with pipes and structures along an alignment.")
        {
            var json  = ReplaceNodeGuids(DynamoTemplates.PIPE_NETWORK_TEMPLATE);
            var graph = ParseTemplateJson(json);
            graph.Name        = graphName;
            graph.Description = description;
            graph.Uuid        = Guid.NewGuid().ToString();
            return graph;
        }

        /// <summary>
        /// Returns a fresh DynamoGraph built from the SURFACE_GRADING_TEMPLATE.
        /// </summary>
        public DynamoGraph CreateSurfaceGradingGraph(
            string graphName   = "Surface Grading Analysis",
            string description = "Compute cut/fill volumes between two surfaces.")
        {
            var json  = ReplaceNodeGuids(DynamoTemplates.SURFACE_GRADING_TEMPLATE);
            var graph = ParseTemplateJson(json);
            graph.Name        = graphName;
            graph.Description = description;
            graph.Uuid        = Guid.NewGuid().ToString();
            return graph;
        }

        /// <summary>
        /// Returns a fresh DynamoGraph built from the ALIGNMENT_FROM_POLYLINE_TEMPLATE.
        /// </summary>
        public DynamoGraph CreateAlignmentFromPolylineGraph(
            string graphName   = "Alignment from Polyline",
            string description = "Convert a polyline to a Civil 3D alignment.")
        {
            var json  = ReplaceNodeGuids(DynamoTemplates.ALIGNMENT_FROM_POLYLINE_TEMPLATE);
            var graph = ParseTemplateJson(json);
            graph.Name        = graphName;
            graph.Description = description;
            graph.Uuid        = Guid.NewGuid().ToString();
            return graph;
        }

        /// <summary>
        /// Returns a fresh DynamoGraph built from the PROFILE_FROM_SURFACE_TEMPLATE.
        /// </summary>
        public DynamoGraph CreateProfileFromSurfaceGraph(
            string graphName   = "Profile from Surface",
            string description = "Create an existing ground profile by sampling a surface.")
        {
            var json  = ReplaceNodeGuids(DynamoTemplates.PROFILE_FROM_SURFACE_TEMPLATE);
            var graph = ParseTemplateJson(json);
            graph.Name        = graphName;
            graph.Description = description;
            graph.Uuid        = Guid.NewGuid().ToString();
            return graph;
        }

        // ------------------------------------------------------------------ //
        //  Programmatic graph builders (no template dependency)
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Builds a Dynamo graph programmatically that:
        ///  1. Gets all alignments and surfaces
        ///  2. Selects the first of each by index
        ///  3. Runs a Python script node that samples the surface along the
        ///     alignment and reports station/elevation pairs
        /// </summary>
        public DynamoGraph BuildAlignmentSurfaceSamplerGraph(
            string graphName = "Alignment Surface Sampler",
            double sampleInterval = 5.0)
        {
            var graph = new DynamoGraph
            {
                Name        = graphName,
                Description = "Sample surface elevations at regular intervals along an alignment.",
            };

            // --- Nodes ---
            var nGetAlignments = NodeFactory.GetAlignments(0, 0);
            var nGetSurfaces   = NodeFactory.GetSurfaces(0, 140);

            var cbIdxA = NodeFactory.CodeBlock("0;", 0, 280);
            var cbIdxS = NodeFactory.CodeBlock("0;", 0, 380);

            var nPickAlign  = MakeListGetItemAtIndex("Pick Alignment", 260, 0);
            var nPickSurf   = MakeListGetItemAtIndex("Pick Surface",   260, 140);

            var cbInterval = NodeFactory.CodeBlock($"{sampleInterval};", 0, 460);

            var pythonCode = PythonDynamoGenerator.BuildSurfaceSamplerScript();
            var nPython = NodeFactory.PythonScript(pythonCode, 540, 0,
                "Surface Sampler",
                "Sample surface elevations along alignment",
                inputs: new[]
                {
                    (Guid.NewGuid().ToString(), "IN[0]", "alignment"),
                    (Guid.NewGuid().ToString(), "IN[1]", "surface"),
                    (Guid.NewGuid().ToString(), "IN[2]", "interval"),
                },
                outputs: new[] { (Guid.NewGuid().ToString(), "OUT", "Station/elevation list") });

            graph.Nodes.AddRange(new[] { nGetAlignments, nGetSurfaces, cbIdxA, cbIdxS, nPickAlign, nPickSurf, cbInterval, nPython });

            // --- Wires ---
            // alignments -> pick alignment list
            graph.Connect(nGetAlignments.Outputs[0].Id, nPickAlign.Inputs[0].Id);
            // idx 0 -> alignment index
            graph.Connect(cbIdxA.Outputs[0].Id, nPickAlign.Inputs[1].Id);
            // surfaces -> pick surface list
            graph.Connect(nGetSurfaces.Outputs[0].Id, nPickSurf.Inputs[0].Id);
            // idx 0 -> surface index
            graph.Connect(cbIdxS.Outputs[0].Id, nPickSurf.Inputs[1].Id);
            // selected alignment -> python IN[0]
            graph.Connect(nPickAlign.Outputs[0].Id, nPython.Inputs[0].Id);
            // selected surface -> python IN[1]
            graph.Connect(nPickSurf.Outputs[0].Id, nPython.Inputs[1].Id);
            // interval -> python IN[2]
            graph.Connect(cbInterval.Outputs[0].Id, nPython.Inputs[2].Id);

            return graph.SyncNodeViews();
        }

        /// <summary>
        /// Builds a Dynamo graph that iterates all pipes in all networks and
        /// writes a Watch node output containing pipe name, diameter, material,
        /// invert in and invert out.
        /// </summary>
        public DynamoGraph BuildPipeInventoryGraph(string graphName = "Pipe Network Inventory")
        {
            var graph = new DynamoGraph
            {
                Name        = graphName,
                Description = "List all pipes in all networks with key parameters.",
            };

            var nGetNetworks = NodeFactory.GetPipeNetworks(0, 0);

            var pythonCode = PythonDynamoGenerator.BuildPipeInventoryScript();
            var nPython = NodeFactory.PythonScript(pythonCode, 300, 0,
                "Pipe Inventory",
                "Collect pipe data from all networks",
                inputs:  new[] { (Guid.NewGuid().ToString(), "IN[0]", "networks") },
                outputs: new[] { (Guid.NewGuid().ToString(), "OUT", "Pipe data rows") });

            var nWatch = MakeWatchNode("Pipe Data", 600, 0);

            graph.Nodes.AddRange(new[] { nGetNetworks, nPython, nWatch });
            graph.Connect(nGetNetworks.Outputs[0].Id, nPython.Inputs[0].Id);
            graph.Connect(nPython.Outputs[0].Id, nWatch.Inputs[0].Id);

            return graph.SyncNodeViews();
        }

        /// <summary>
        /// Builds a Dynamo graph that extracts corridor section data
        /// (offsets and elevations) for a given station range.
        /// </summary>
        public DynamoGraph BuildCorridorSectionExtractorGraph(
            string graphName       = "Corridor Section Extractor",
            double startStation    = 0.0,
            double endStation      = 1000.0,
            double stationInterval = 25.0)
        {
            var graph = new DynamoGraph
            {
                Name        = graphName,
                Description = "Extract corridor cross-section points at regular station intervals.",
            };

            var nGetCorridors = NodeFactory.GetCorridors(0, 0);
            var cbParams      = NodeFactory.CodeBlock(
                $"0;  // corridor index\n{startStation};  // start station\n{endStation};  // end station\n{stationInterval};  // station interval",
                0, 140,
                outputs: new[]
                {
                    (Guid.NewGuid().ToString(), ">>", "corridor idx"),
                    (Guid.NewGuid().ToString(), ">>", "start sta"),
                    (Guid.NewGuid().ToString(), ">>", "end sta"),
                    (Guid.NewGuid().ToString(), ">>", "interval"),
                });

            var nPickCorridor = MakeListGetItemAtIndex("Pick Corridor", 280, 0);

            var pythonCode = PythonDynamoGenerator.BuildCorridorSectionExtractorScript();
            var nPython = NodeFactory.PythonScript(pythonCode, 520, 0,
                "Section Extractor",
                "Extract cross-section offset/elevation data",
                inputs: new[]
                {
                    (Guid.NewGuid().ToString(), "IN[0]", "corridor"),
                    (Guid.NewGuid().ToString(), "IN[1]", "start_station"),
                    (Guid.NewGuid().ToString(), "IN[2]", "end_station"),
                    (Guid.NewGuid().ToString(), "IN[3]", "interval"),
                },
                outputs: new[] { (Guid.NewGuid().ToString(), "OUT", "Section data") });

            graph.Nodes.AddRange(new[] { nGetCorridors, cbParams, nPickCorridor, nPython });

            graph.Connect(nGetCorridors.Outputs[0].Id, nPickCorridor.Inputs[0].Id);
            graph.Connect(cbParams.Outputs[0].Id,      nPickCorridor.Inputs[1].Id);
            graph.Connect(nPickCorridor.Outputs[0].Id, nPython.Inputs[0].Id);
            graph.Connect(cbParams.Outputs[1].Id,      nPython.Inputs[1].Id);
            graph.Connect(cbParams.Outputs[2].Id,      nPython.Inputs[2].Id);
            graph.Connect(cbParams.Outputs[3].Id,      nPython.Inputs[3].Id);

            return graph.SyncNodeViews();
        }

        /// <summary>
        /// Builds a batch-parameter-update graph that reads a CSV file
        /// (path, parameter-name, new-value) and applies values to Civil 3D objects.
        /// </summary>
        public DynamoGraph BuildBatchParameterUpdateGraph(
            string csvFilePath = @"C:\Civil3D\batch_params.csv",
            string graphName   = "Batch Parameter Update")
        {
            var graph = new DynamoGraph
            {
                Name        = graphName,
                Description = "Read a CSV and apply parameter values to Civil 3D objects in bulk.",
            };

            var cbCsvPath = NodeFactory.CodeBlock($"\"{csvFilePath.Replace(@"\", @"\\")}\";", 0, 0,
                outputs: new[] { (Guid.NewGuid().ToString(), ">>", "csv path") });

            var pythonCode = PythonDynamoGenerator.BuildBatchParameterUpdateScript();
            var nPython = NodeFactory.PythonScript(pythonCode, 300, 0,
                "Batch Parameter Update",
                "Apply CSV-driven parameter values",
                inputs:  new[] { (Guid.NewGuid().ToString(), "IN[0]", "csv_path") },
                outputs: new[] { (Guid.NewGuid().ToString(), "OUT", "Update results") });

            graph.Nodes.AddRange(new[] { cbCsvPath, nPython });
            graph.Connect(cbCsvPath.Outputs[0].Id, nPython.Inputs[0].Id);

            return graph.SyncNodeViews();
        }

        // ------------------------------------------------------------------ //
        //  Graph manipulation utilities
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Replaces every stable UUID in the template JSON with a fresh Guid,
        /// ensuring graphs generated from templates are unique.
        /// </summary>
        public static string ReplaceNodeGuids(string templateJson)
        {
            // Walk the JSON and replace well-formed GUIDs with new ones.
            // We build a replacement map first so cross-references stay
            // consistent (the same old GUID always maps to the same new GUID).
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Extract all UUIDs from the JSON (simple regex-free approach: split
            // by common delimiters and scan for guid-shaped tokens).
            var parts = templateJson.Split('"');
            foreach (var part in parts)
            {
                if (Guid.TryParse(part, out _) && !mapping.ContainsKey(part))
                    mapping[part] = Guid.NewGuid().ToString();
            }

            // Apply substitutions
            var sb = new StringBuilder(templateJson);
            foreach (var (oldGuid, newGuid) in mapping)
                sb.Replace(oldGuid, newGuid);

            return sb.ToString();
        }

        /// <summary>
        /// Parse a complete Dynamo graph JSON string into a <see cref="DynamoGraph"/> object.
        /// Returns a minimal stub graph if parsing fails.
        /// </summary>
        public static DynamoGraph ParseTemplateJson(string json)
        {
            try
            {
                var opts  = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var graph = JsonSerializer.Deserialize<DynamoGraph>(json, opts);
                return graph ?? new DynamoGraph();
            }
            catch
            {
                // Fail-safe: return empty graph rather than throwing
                return new DynamoGraph();
            }
        }

        /// <summary>
        /// Save the given template directly to a file with fresh GUIDs.
        /// </summary>
        public void SaveTemplate(string templateName, string outputPath)
        {
            var templateJson = DynamoTemplates.GetTemplate(templateName);
            var freshJson    = ReplaceNodeGuids(templateJson);
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir!);
            File.WriteAllText(outputPath, freshJson, Encoding.UTF8);
        }

        /// <summary>
        /// Save all five templates into <paramref name="outputDirectory"/>,
        /// one file per template, named after the template constant.
        /// </summary>
        public void SaveAllTemplates(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            foreach (var name in DynamoTemplates.AllTemplateNames)
            {
                var filePath = Path.Combine(outputDirectory, $"{name}.dyn");
                SaveTemplate(name, filePath);
            }
        }

        // ------------------------------------------------------------------ //
        //  Private helpers
        // ------------------------------------------------------------------ //

        private static DynNode MakeListGetItemAtIndex(string description, double x, double y)
        {
            var inList  = Guid.NewGuid().ToString();
            var inIndex = Guid.NewGuid().ToString();
            var outItem = Guid.NewGuid().ToString();
            return NodeFactory.ZeroTouch(
                functionSignature: "List.GetItemAtIndex@var[]..[],int",
                name:        "List.GetItemAtIndex",
                description: description,
                x: x, y: y,
                inputs:  new[] { (inList, "list", "List"), (inIndex, "index", "Index") },
                outputs: new[] { (outItem, "item", "Item at index") });
        }

        private static DynNode MakeWatchNode(string description, double x, double y)
        {
            var inPort  = Guid.NewGuid().ToString();
            var outPort = Guid.NewGuid().ToString();
            return new DynNode
            {
                ConcreteType      = "Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore",
                NodeType          = "FunctionNode",
                FunctionSignature = "Watch.WatchValue@var[]..[]",
                Name              = "Watch",
                Description       = description,
                Replication       = "Disabled",
                X = x, Y = y,
                Inputs  = new List<DynPort> { new DynPort { Id = inPort,  Name = "var[]..[]", Description = "Watch input" } },
                Outputs = new List<DynPort> { new DynPort { Id = outPort, Name = "var[]..[]", Description = "Watch output" } },
            };
        }
    }
}
