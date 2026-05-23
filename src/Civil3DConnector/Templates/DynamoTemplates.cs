// DynamoTemplates.cs
// Static template strings for common Civil 3D Dynamo workflows.
// These JSON skeletons are valid Dynamo 2.x / 3.x (.dyn) graph format and can
// be opened directly by the Dynamo player embedded in Civil 3D 2025-2027.
//
// Design notes:
//  - Every template is a complete, minimal graph that demonstrates the workflow.
//  - UUIDs are stable placeholder values; callers should replace them when
//    generating unique graphs (see DynamoScriptGenerator.ReplaceNodeGuids).
//  - Python script code blocks use CPython 3 syntax and include both
//    IronPython 2 and CPython 3 compatibility guards where needed.

using System;

namespace Civil3DConnector.Templates
{
    /// <summary>
    /// Provides static template strings for common Civil 3D Dynamo workflows.
    /// Each constant is a complete Dynamo 2.x/3.x JSON graph (.dyn).
    /// </summary>
    public static class DynamoTemplates
    {
        // ------------------------------------------------------------------ //
        //  CORRIDOR CREATION TEMPLATE
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Creates a corridor from an existing baseline alignment, profile, and
        /// assembly. Demonstrates Civil3D.Corridor.ByAlignmentProfileAssembly.
        /// </summary>
        public const string CORRIDOR_CREATION_TEMPLATE = @"{
  ""Uuid"": ""c1000001-0000-0000-0000-000000000001"",
  ""IsCustomNode"": false,
  ""Description"": ""Create a Civil 3D corridor from alignment, profile, and assembly."",
  ""Name"": ""Corridor Creation"",
  ""ElementResolver"": { ""ResolutionMap"": {} },
  ""Inputs"": [],
  ""Outputs"": [],
  ""Nodes"": [
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.CustomNodes.Function, DynamoCore"",
      ""FunctionSignature"": ""a0000001-0000-0000-0000-000000000001"",
      ""FunctionType"": ""Graph"",
      ""NodeType"": ""FunctionNode"",
      ""Id"": ""n0000001-0000-0000-0000-000000000001"",
      ""Inputs"": [],
      ""Outputs"": [{ ""Id"": ""p0000001"", ""Name"": ""Alignments"", ""Description"": ""All alignments in document"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Get all alignments from the active document"",
      ""Name"": ""Civil3D.Alignment.GetAlignments"",
      ""X"": 0.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""Civil3D.Alignment.Profiles@Civil3D.Alignment"",
      ""Id"": ""n0000001-0000-0000-0000-000000000002"",
      ""Inputs"": [{ ""Id"": ""p0000002"", ""Name"": ""alignment"", ""Description"": ""Civil3D.Alignment"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Outputs"": [{ ""Id"": ""p0000003"", ""Name"": ""profiles"", ""Description"": ""List of profiles on this alignment"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Auto"",
      ""Description"": ""Get profiles for alignment"",
      ""Name"": ""Civil3D.Alignment.Profiles"",
      ""X"": 280.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""Civil3D.Assembly.GetAssemblies"",
      ""Id"": ""n0000001-0000-0000-0000-000000000003"",
      ""Inputs"": [],
      ""Outputs"": [{ ""Id"": ""p0000004"", ""Name"": ""assemblies"", ""Description"": ""All assemblies"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Get all assemblies in the document"",
      ""Name"": ""Civil3D.Assembly.GetAssemblies"",
      ""X"": 0.0,
      ""Y"": 120.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore"",
      ""NodeType"": ""CodeBlockNode"",
      ""Code"": ""0;"",
      ""Id"": ""n0000001-0000-0000-0000-000000000004"",
      ""Inputs"": [],
      ""Outputs"": [{ ""Id"": ""p0000005"", ""Name"": "">>"", ""Description"": ""Index 0 - first alignment"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Index selector"",
      ""X"": 0.0,
      ""Y"": 240.0
    },
    {
      ""ConcreteType"": ""DSCoreNodesUI.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""List.GetItemAtIndex@var[]..[],int"",
      ""Id"": ""n0000001-0000-0000-0000-000000000005"",
      ""Inputs"": [
        { ""Id"": ""p0000006"", ""Name"": ""list"", ""Description"": ""List"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p0000007"", ""Name"": ""index"", ""Description"": ""Index"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Outputs"": [{ ""Id"": ""p0000008"", ""Name"": ""item"", ""Description"": ""Item at index"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Auto"",
      ""Description"": ""Get alignment at index 0"",
      ""Name"": ""List.GetItemAtIndex"",
      ""X"": 280.0,
      ""Y"": 120.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""Civil3D.Corridor.ByAlignmentProfileAssembly@Civil3D.Alignment,Civil3D.Profile,Civil3D.Assembly,string"",
      ""Id"": ""n0000001-0000-0000-0000-000000000006"",
      ""Inputs"": [
        { ""Id"": ""p0000009"", ""Name"": ""alignment"",  ""Description"": ""Baseline alignment"",  ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p0000010"", ""Name"": ""profile"",    ""Description"": ""Baseline profile"",    ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p0000011"", ""Name"": ""assembly"",  ""Description"": ""Assembly"",             ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p0000012"", ""Name"": ""name"",      ""Description"": ""Corridor name"",        ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Outputs"": [{ ""Id"": ""p0000013"", ""Name"": ""corridor"", ""Description"": ""New corridor"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Auto"",
      ""Description"": ""Create a Civil 3D corridor"",
      ""Name"": ""Civil3D.Corridor.ByAlignmentProfileAssembly"",
      ""X"": 600.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore"",
      ""NodeType"": ""CodeBlockNode"",
      ""Code"": ""\""My Corridor\"";"",
      ""Id"": ""n0000001-0000-0000-0000-000000000007"",
      ""Inputs"": [],
      ""Outputs"": [{ ""Id"": ""p0000014"", ""Name"": "">>"", ""Description"": ""Corridor name string"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Corridor name"",
      ""X"": 280.0,
      ""Y"": 240.0
    }
  ],
  ""Connectors"": [
    { ""Start"": ""p0000001"", ""End"": ""p0000006"", ""Id"": ""w0001"" },
    { ""Start"": ""p0000005"", ""End"": ""p0000007"", ""Id"": ""w0002"" },
    { ""Start"": ""p0000008"", ""End"": ""p0000009"", ""Id"": ""w0003"" },
    { ""Start"": ""p0000003"", ""End"": ""p0000010"", ""Id"": ""w0004"" },
    { ""Start"": ""p0000004"", ""End"": ""p0000007"", ""Id"": ""w0005"" },
    { ""Start"": ""p0000004"", ""End"": ""p0000002"", ""Id"": ""w0006"" },
    { ""Start"": ""p0000004"", ""End"": ""p0000011"", ""Id"": ""w0007"" },
    { ""Start"": ""p0000014"", ""End"": ""p0000012"", ""Id"": ""w0008"" }
  ],
  ""Dependencies"": [],
  ""NodeLibraryDependencies"": [
    {
      ""Name"": ""Civil3DToolkit"",
      ""Version"": ""1.0.0"",
      ""ReferenceType"": ""Package"",
      ""Nodes"": [""n0000001-0000-0000-0000-000000000001"", ""n0000001-0000-0000-0000-000000000002"", ""n0000001-0000-0000-0000-000000000003"", ""n0000001-0000-0000-0000-000000000006""]
    }
  ],
  ""Thumbnail"": """",
  ""GraphDocumentationURL"": null,
  ""ExtensionWorkspaceData"": [],
  ""Author"": ""Civil3DConnector"",
  ""Linting"": { ""activeLinter"": ""None"", ""activeLinterId"": ""7b75fb44-43fd-4631-a878-29f4d5d8399a"", ""warningCount"": 0, ""errorCount"": 0 },
  ""Bindings"": [],
  ""View"": {
    ""Dynamo"": { ""ScaleFactor"": 1.0, ""HasRunWithoutCrash"": false, ""IsVisibleInDynamoLibrary"": true, ""Version"": ""2.18.0.3593"", ""RunType"": ""Manual"", ""RunPeriod"": ""1000"" },
    ""Camera"": { ""Name"": ""Background Preview"", ""EyeX"": -17.0, ""EyeY"": 24.0, ""EyeZ"": 50.0, ""LookX"": 12.0, ""LookY"": -13.0, ""LookZ"": -58.0, ""UpX"": 0.0, ""UpY"": 1.0, ""UpZ"": 0.0 },
    ""ConnectorPins"": [],
    ""NodeViews"": [
      { ""ShowGeometry"": true, ""Name"": ""Civil3D.Alignment.GetAlignments"", ""Id"": ""n0000001-0000-0000-0000-000000000001"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""Civil3D.Alignment.Profiles"",       ""Id"": ""n0000001-0000-0000-0000-000000000002"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 280.0, ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""Civil3D.Assembly.GetAssemblies"",   ""Id"": ""n0000001-0000-0000-0000-000000000003"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 120.0 },
      { ""ShowGeometry"": true, ""Name"": ""Code Block"",                       ""Id"": ""n0000001-0000-0000-0000-000000000004"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 240.0 },
      { ""ShowGeometry"": true, ""Name"": ""List.GetItemAtIndex"",              ""Id"": ""n0000001-0000-0000-0000-000000000005"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 280.0, ""Y"": 120.0 },
      { ""ShowGeometry"": true, ""Name"": ""Civil3D.Corridor.ByAlignmentProfileAssembly"", ""Id"": ""n0000001-0000-0000-0000-000000000006"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 600.0, ""Y"": 0.0 },
      { ""ShowGeometry"": true, ""Name"": ""Code Block"",                       ""Id"": ""n0000001-0000-0000-0000-000000000007"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 280.0, ""Y"": 240.0 }
    ],
    ""Annotations"": [],
    ""Notes"": [],
    ""Groups"": []
  }
}";

        // ------------------------------------------------------------------ //
        //  PIPE NETWORK TEMPLATE
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Creates a storm-drain pipe network with pipes and structures placed
        /// along an alignment at a user-specified spacing.
        /// </summary>
        public const string PIPE_NETWORK_TEMPLATE = @"{
  ""Uuid"": ""c2000001-0000-0000-0000-000000000002"",
  ""IsCustomNode"": false,
  ""Description"": ""Create a pipe network with pipes and structures along an alignment."",
  ""Name"": ""Pipe Network Creation"",
  ""ElementResolver"": { ""ResolutionMap"": {} },
  ""Inputs"": [],
  ""Outputs"": [],
  ""Nodes"": [
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""Civil3D.Alignment.GetAlignments"",
      ""Id"": ""n0000002-0000-0000-0000-000000000001"",
      ""Inputs"": [],
      ""Outputs"": [{ ""Id"": ""p2000001"", ""Name"": ""alignments"", ""Description"": ""All alignments"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Get all alignments"",
      ""Name"": ""Civil3D.Alignment.GetAlignments"",
      ""X"": 0.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.PythonNode, DSCPython"",
      ""NodeType"": ""PythonScriptNode"",
      ""Engine"": ""CPython3"",
      ""EngineName"": ""CPython3"",
      ""VariableInputPorts"": true,
      ""Code"": ""import clr\nclr.AddReference('AeccDbMgd')\nclr.AddReference('AecBaseMgd')\nclr.AddReference('AutoCAD.ApplicationServices.Core')\nfrom Autodesk.AutoCAD.ApplicationServices import Application\nfrom Autodesk.Civil.DatabaseServices import Network, Pipe, Structure\nfrom Autodesk.Civil.DatabaseServices import PipeNetwork\nfrom Autodesk.AutoCAD.DatabaseServices import Transaction\n\nalignment = IN[0]\nspacing   = IN[1]   # structure spacing in metres\npipe_part  = IN[2]  # pipe part family name\nstruct_part = IN[3] # structure part family name\nnetwork_name = IN[4]\n\ndoc = Application.DocumentManager.MdiActiveDocument\ndb  = doc.Database\neditor = doc.Editor\n\ndef create_pipe_network(alignment, spacing, pipe_part, struct_part, net_name):\n    results = {'network': None, 'pipe_count': 0, 'structure_count': 0, 'errors': []}\n    try:\n        with doc.LockDocument():\n            with db.TransactionManager.StartTransaction() as tr:\n                # Create the network\n                net_id = PipeNetwork.Create(db, net_name)\n                network = tr.GetObject(net_id, OpenMode.ForWrite)\n                network.ReferenceAlignmentId = alignment.InternalObjectId\n\n                # Sample stations along alignment\n                start_sta = alignment.StartStation\n                end_sta   = alignment.EndStation\n                stations  = []\n                sta = start_sta\n                while sta <= end_sta:\n                    stations.append(sta)\n                    sta += spacing\n                if stations[-1] < end_sta:\n                    stations.append(end_sta)\n\n                prev_struct_id = None\n                for i, sta in enumerate(stations):\n                    pt = alignment.GetPointAtDist(sta - start_sta)\n                    # Create structure\n                    struct_id = Structure.Create(network, struct_part, pt, 0.0)\n                    results['structure_count'] += 1\n                    if prev_struct_id is not None:\n                        # Create pipe between previous and current structure\n                        pipe_id = Pipe.Create(network, pipe_part, prev_struct_id, struct_id)\n                        results['pipe_count'] += 1\n                    prev_struct_id = struct_id\n\n                tr.Commit()\n                results['network'] = network_name\n    except Exception as ex:\n        results['errors'].append(str(ex))\n    return results\n\nOUT = create_pipe_network(alignment, spacing, pipe_part, struct_part, network_name)\n"",
      ""Id"": ""n0000002-0000-0000-0000-000000000002"",
      ""Inputs"": [
        { ""Id"": ""p2000002"", ""Name"": ""IN[0]"", ""Description"": ""alignment"",    ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p2000003"", ""Name"": ""IN[1]"", ""Description"": ""spacing"",      ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p2000004"", ""Name"": ""IN[2]"", ""Description"": ""pipe_part"",    ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p2000005"", ""Name"": ""IN[3]"", ""Description"": ""struct_part"",  ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p2000006"", ""Name"": ""IN[4]"", ""Description"": ""network_name"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Outputs"": [{ ""Id"": ""p2000007"", ""Name"": ""OUT"", ""Description"": ""Result dict"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Create pipe network along alignment"",
      ""Name"": ""Python Script"",
      ""X"": 400.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore"",
      ""NodeType"": ""CodeBlockNode"",
      ""Code"": ""20.0;\n\""Concrete Pipe - 450mm\"";\n\""Circular Structure - 1200mm\"";\n\""Storm Drain Network\"";  "",
      ""Id"": ""n0000002-0000-0000-0000-000000000003"",
      ""Inputs"": [],
      ""Outputs"": [
        { ""Id"": ""p2000008"", ""Name"": "">>"", ""Description"": ""spacing"",      ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p2000009"", ""Name"": "">>"", ""Description"": ""pipe_part"",    ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p2000010"", ""Name"": "">>"", ""Description"": ""struct_part"",  ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p2000011"", ""Name"": "">>"", ""Description"": ""network_name"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Replication"": ""Disabled"",
      ""Description"": ""Parameters"",
      ""X"": 0.0,
      ""Y"": 120.0
    }
  ],
  ""Connectors"": [
    { ""Start"": ""p2000001"", ""End"": ""p2000002"", ""Id"": ""w2001"" },
    { ""Start"": ""p2000008"", ""End"": ""p2000003"", ""Id"": ""w2002"" },
    { ""Start"": ""p2000009"", ""End"": ""p2000004"", ""Id"": ""w2003"" },
    { ""Start"": ""p2000010"", ""End"": ""p2000005"", ""Id"": ""w2004"" },
    { ""Start"": ""p2000011"", ""End"": ""p2000006"", ""Id"": ""w2005"" }
  ],
  ""Dependencies"": [],
  ""NodeLibraryDependencies"": [],
  ""Thumbnail"": """",
  ""GraphDocumentationURL"": null,
  ""ExtensionWorkspaceData"": [],
  ""Author"": ""Civil3DConnector"",
  ""Linting"": { ""activeLinter"": ""None"", ""activeLinterId"": ""7b75fb44-43fd-4631-a878-29f4d5d8399a"", ""warningCount"": 0, ""errorCount"": 0 },
  ""Bindings"": [],
  ""View"": {
    ""Dynamo"": { ""ScaleFactor"": 1.0, ""HasRunWithoutCrash"": false, ""IsVisibleInDynamoLibrary"": true, ""Version"": ""2.18.0.3593"", ""RunType"": ""Manual"", ""RunPeriod"": ""1000"" },
    ""Camera"": { ""Name"": ""Background Preview"", ""EyeX"": -17.0, ""EyeY"": 24.0, ""EyeZ"": 50.0, ""LookX"": 12.0, ""LookY"": -13.0, ""LookZ"": -58.0, ""UpX"": 0.0, ""UpY"": 1.0, ""UpZ"": 0.0 },
    ""ConnectorPins"": [],
    ""NodeViews"": [
      { ""ShowGeometry"": true, ""Name"": ""Civil3D.Alignment.GetAlignments"", ""Id"": ""n0000002-0000-0000-0000-000000000001"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 0.0 },
      { ""ShowGeometry"": true, ""Name"": ""Python Script"",                   ""Id"": ""n0000002-0000-0000-0000-000000000002"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 400.0, ""Y"": 0.0 },
      { ""ShowGeometry"": true, ""Name"": ""Code Block"",                      ""Id"": ""n0000002-0000-0000-0000-000000000003"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 120.0 }
    ],
    ""Annotations"": [],
    ""Notes"": [],
    ""Groups"": []
  }
}";

        // ------------------------------------------------------------------ //
        //  SURFACE GRADING TEMPLATE
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Computes cut/fill volumes between an existing and proposed surface,
        /// exports a point grid, and optionally creates contour polylines.
        /// </summary>
        public const string SURFACE_GRADING_TEMPLATE = @"{
  ""Uuid"": ""c3000001-0000-0000-0000-000000000003"",
  ""IsCustomNode"": false,
  ""Description"": ""Compute cut/fill volumes between two surfaces and export analysis data."",
  ""Name"": ""Surface Grading Analysis"",
  ""ElementResolver"": { ""ResolutionMap"": {} },
  ""Inputs"": [],
  ""Outputs"": [],
  ""Nodes"": [
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""Civil3D.Surface.GetSurfaces"",
      ""Id"": ""n0000003-0000-0000-0000-000000000001"",
      ""Inputs"": [],
      ""Outputs"": [{ ""Id"": ""p3000001"", ""Name"": ""surfaces"", ""Description"": ""All TIN surfaces in document"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Get all surfaces"",
      ""Name"": ""Civil3D.Surface.GetSurfaces"",
      ""X"": 0.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.PythonNode, DSCPython"",
      ""NodeType"": ""PythonScriptNode"",
      ""Engine"": ""CPython3"",
      ""EngineName"": ""CPython3"",
      ""VariableInputPorts"": true,
      ""Code"": ""import clr\nclr.AddReference('AeccDbMgd')\nclr.AddReference('AecBaseMgd')\nclr.AddReference('AutoCAD.ApplicationServices.Core')\nfrom Autodesk.AutoCAD.ApplicationServices import Application\nfrom Autodesk.Civil.DatabaseServices import TinSurface, TinVolumeSurface\nfrom Autodesk.AutoCAD.Geometry import Point3d\nfrom Autodesk.AutoCAD.DatabaseServices import OpenMode\nimport math\n\nexisting_surface = IN[0]   # existing ground TIN surface\nproposed_surface = IN[1]   # proposed finished grade TIN surface\nvolume_name      = IN[2]   # name for the volume surface\ngrid_spacing     = IN[3]   # grid spacing for point export (metres)\n\ndoc = Application.DocumentManager.MdiActiveDocument\ndb  = doc.Database\n\ndef analyse_surfaces(existing, proposed, vol_name, grid_sp):\n    results = {\n        'cut_volume':  0.0,\n        'fill_volume': 0.0,\n        'net_volume':  0.0,\n        'grid_points': [],\n        'errors': []\n    }\n    try:\n        ex_obj = existing.InternalDBObject\n        pr_obj = proposed.InternalDBObject\n\n        # --- Volume surface via AutoCAD Civil 3D API ---\n        with doc.LockDocument():\n            with db.TransactionManager.StartTransaction() as tr:\n                vol_id = TinVolumeSurface.Create(db, vol_name)\n                vol    = tr.GetObject(vol_id, OpenMode.ForWrite)\n                vol.SetBaseAndComparisonSurfaces(existing.InternalObjectId,\n                                                  proposed.InternalObjectId)\n                vol.Rebuild()\n\n                props = vol.GetVolumeProperties()\n                results['cut_volume']  = round(props.CutVolume,  3)\n                results['fill_volume'] = round(props.FillVolume, 3)\n                results['net_volume']  = round(props.NetVolume,  3)\n\n                # --- Sample grid points from existing surface ---\n                ext  = ex_obj.GeometricExtents\n                minX = ext.MinPoint.X\n                minY = ext.MinPoint.Y\n                maxX = ext.MaxPoint.X\n                maxY = ext.MaxPoint.Y\n                x = minX\n                while x <= maxX:\n                    y = minY\n                    while y <= maxY:\n                        try:\n                            z_ex = ex_obj.FindElevationAtXY(x, y)\n                            z_pr = pr_obj.FindElevationAtXY(x, y)\n                            results['grid_points'].append({\n                                'x': round(x, 3),\n                                'y': round(y, 3),\n                                'z_existing': round(z_ex, 3),\n                                'z_proposed': round(z_pr, 3),\n                                'delta':      round(z_pr - z_ex, 3)\n                            })\n                        except Exception:\n                            pass\n                        y += grid_sp\n                    x += grid_sp\n\n                tr.Commit()\n    except Exception as ex:\n        results['errors'].append(str(ex))\n    return results\n\nOUT = analyse_surfaces(existing_surface, proposed_surface, volume_name, grid_spacing)\n"",
      ""Id"": ""n0000003-0000-0000-0000-000000000002"",
      ""Inputs"": [
        { ""Id"": ""p3000002"", ""Name"": ""IN[0]"", ""Description"": ""existing surface"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p3000003"", ""Name"": ""IN[1]"", ""Description"": ""proposed surface"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p3000004"", ""Name"": ""IN[2]"", ""Description"": ""volume name"",     ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p3000005"", ""Name"": ""IN[3]"", ""Description"": ""grid spacing"",    ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Outputs"": [{ ""Id"": ""p3000006"", ""Name"": ""OUT"", ""Description"": ""Volume and grid results"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Compute cut/fill volumes and sample grid"",
      ""Name"": ""Python Script"",
      ""X"": 400.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore"",
      ""NodeType"": ""CodeBlockNode"",
      ""Code"": ""// Select surfaces by index\nexisting = List.GetItemAtIndex(surfaces, 0);\nproposed = List.GetItemAtIndex(surfaces, 1);"",
      ""Id"": ""n0000003-0000-0000-0000-000000000003"",
      ""Inputs"": [{ ""Id"": ""p3000007"", ""Name"": ""surfaces"", ""Description"": ""Surface list"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Outputs"": [
        { ""Id"": ""p3000008"", ""Name"": ""existing"", ""Description"": ""Existing surface"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p3000009"", ""Name"": ""proposed"", ""Description"": ""Proposed surface"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Replication"": ""Disabled"",
      ""Description"": ""Split surface list"",
      ""X"": 200.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore"",
      ""NodeType"": ""CodeBlockNode"",
      ""Code"": ""\""Volume Surface\"";\n5.0;"",
      ""Id"": ""n0000003-0000-0000-0000-000000000004"",
      ""Inputs"": [],
      ""Outputs"": [
        { ""Id"": ""p3000010"", ""Name"": "">>"", ""Description"": ""volume name"",  ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p3000011"", ""Name"": "">>"", ""Description"": ""grid spacing"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Replication"": ""Disabled"",
      ""Description"": ""Analysis parameters"",
      ""X"": 200.0,
      ""Y"": 120.0
    }
  ],
  ""Connectors"": [
    { ""Start"": ""p3000001"", ""End"": ""p3000007"", ""Id"": ""w3001"" },
    { ""Start"": ""p3000008"", ""End"": ""p3000002"", ""Id"": ""w3002"" },
    { ""Start"": ""p3000009"", ""End"": ""p3000003"", ""Id"": ""w3003"" },
    { ""Start"": ""p3000010"", ""End"": ""p3000004"", ""Id"": ""w3004"" },
    { ""Start"": ""p3000011"", ""End"": ""p3000005"", ""Id"": ""w3005"" }
  ],
  ""Dependencies"": [],
  ""NodeLibraryDependencies"": [],
  ""Thumbnail"": """",
  ""GraphDocumentationURL"": null,
  ""ExtensionWorkspaceData"": [],
  ""Author"": ""Civil3DConnector"",
  ""Linting"": { ""activeLinter"": ""None"", ""activeLinterId"": ""7b75fb44-43fd-4631-a878-29f4d5d8399a"", ""warningCount"": 0, ""errorCount"": 0 },
  ""Bindings"": [],
  ""View"": {
    ""Dynamo"": { ""ScaleFactor"": 1.0, ""HasRunWithoutCrash"": false, ""IsVisibleInDynamoLibrary"": true, ""Version"": ""2.18.0.3593"", ""RunType"": ""Manual"", ""RunPeriod"": ""1000"" },
    ""Camera"": { ""Name"": ""Background Preview"", ""EyeX"": -17.0, ""EyeY"": 24.0, ""EyeZ"": 50.0, ""LookX"": 12.0, ""LookY"": -13.0, ""LookZ"": -58.0, ""UpX"": 0.0, ""UpY"": 1.0, ""UpZ"": 0.0 },
    ""ConnectorPins"": [],
    ""NodeViews"": [
      { ""ShowGeometry"": true, ""Name"": ""Civil3D.Surface.GetSurfaces"", ""Id"": ""n0000003-0000-0000-0000-000000000001"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""Python Script"",               ""Id"": ""n0000003-0000-0000-0000-000000000002"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 400.0, ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""Code Block (surface split)"",  ""Id"": ""n0000003-0000-0000-0000-000000000003"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 200.0, ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""Code Block (params)"",         ""Id"": ""n0000003-0000-0000-0000-000000000004"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 200.0, ""Y"": 120.0 }
    ],
    ""Annotations"": [],
    ""Notes"": [],
    ""Groups"": []
  }
}";

        // ------------------------------------------------------------------ //
        //  ALIGNMENT FROM POLYLINE TEMPLATE
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Converts a selected AutoCAD polyline into a Civil 3D alignment,
        /// assigns a design speed, and optionally applies design criteria.
        /// </summary>
        public const string ALIGNMENT_FROM_POLYLINE_TEMPLATE = @"{
  ""Uuid"": ""c4000001-0000-0000-0000-000000000004"",
  ""IsCustomNode"": false,
  ""Description"": ""Create a Civil 3D alignment from a selected AutoCAD polyline."",
  ""Name"": ""Alignment from Polyline"",
  ""ElementResolver"": { ""ResolutionMap"": {} },
  ""Inputs"": [],
  ""Outputs"": [],
  ""Nodes"": [
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""Select.SelectObject"",
      ""Id"": ""n0000004-0000-0000-0000-000000000001"",
      ""Inputs"": [],
      ""Outputs"": [{ ""Id"": ""p4000001"", ""Name"": ""Element"", ""Description"": ""Selected AutoCAD element"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Select a polyline from the drawing"",
      ""Name"": ""Select.SelectObject"",
      ""X"": 0.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.PythonNode, DSCPython"",
      ""NodeType"": ""PythonScriptNode"",
      ""Engine"": ""CPython3"",
      ""EngineName"": ""CPython3"",
      ""VariableInputPorts"": true,
      ""Code"": ""import clr\nclr.AddReference('AeccDbMgd')\nclr.AddReference('AecBaseMgd')\nclr.AddReference('AutoCAD.ApplicationServices.Core')\nfrom Autodesk.AutoCAD.ApplicationServices import Application\nfrom Autodesk.Civil.DatabaseServices import Alignment, AlignmentCreationData\nfrom Autodesk.Civil.DatabaseServices import AlignmentType\nfrom Autodesk.AutoCAD.DatabaseServices import (\n    Transaction, OpenMode, Polyline, Polyline2d\n)\nfrom Autodesk.AutoCAD.Colors import Color\nimport sys\n\npolyline_element = IN[0]\nalignment_name   = IN[1]\ndesign_speed_kph = IN[2]   # design speed in km/h\nlayer_name       = IN[3]\n\ndoc    = Application.DocumentManager.MdiActiveDocument\ndb     = doc.Database\neditor = doc.Editor\n\ndef alignment_from_polyline(poly_elem, name, speed_kph, layer):\n    result = {'alignment_id': None, 'length': 0.0, 'errors': []}\n    try:\n        with doc.LockDocument():\n            with db.TransactionManager.StartTransaction() as tr:\n                # Resolve the polyline\n                poly_id = poly_elem.InternalObjectId\n                poly    = tr.GetObject(poly_id, OpenMode.ForRead)\n                if not isinstance(poly, (Polyline, Polyline2d)):\n                    raise TypeError('Selected object is not a polyline')\n\n                # Build creation data\n                site_id = ObjectId.Null  # no site\n                cd = AlignmentCreationData(name, site_id, layer,\n                                           db.ByLayerLinetypeId,\n                                           AlignmentType.Centerline)\n                cd.ConvertPolylineToAlignment = True\n\n                al_id = Alignment.Create(db, poly_id, cd)\n                al    = tr.GetObject(al_id, OpenMode.ForWrite)\n                al.DesignSpeed = speed_kph\n\n                result['alignment_id'] = str(al_id)\n                result['length']       = round(al.Length, 3)\n                tr.Commit()\n    except Exception as ex:\n        result['errors'].append(str(ex))\n    return result\n\nOUT = alignment_from_polyline(polyline_element, alignment_name,\n                               design_speed_kph, layer_name)\n"",
      ""Id"": ""n0000004-0000-0000-0000-000000000002"",
      ""Inputs"": [
        { ""Id"": ""p4000002"", ""Name"": ""IN[0]"", ""Description"": ""polyline element"",   ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p4000003"", ""Name"": ""IN[1]"", ""Description"": ""alignment name"",    ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p4000004"", ""Name"": ""IN[2]"", ""Description"": ""design speed kph"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p4000005"", ""Name"": ""IN[3]"", ""Description"": ""layer name"",        ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Outputs"": [{ ""Id"": ""p4000006"", ""Name"": ""OUT"", ""Description"": ""Result dict"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Convert polyline to alignment"",
      ""Name"": ""Python Script"",
      ""X"": 400.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore"",
      ""NodeType"": ""CodeBlockNode"",
      ""Code"": ""\""Road Centreline\"";\n60;\n\""C-ROAD-CLNE\"";  "",
      ""Id"": ""n0000004-0000-0000-0000-000000000003"",
      ""Inputs"": [],
      ""Outputs"": [
        { ""Id"": ""p4000007"", ""Name"": "">>"", ""Description"": ""alignment name"",    ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p4000008"", ""Name"": "">>"", ""Description"": ""design speed kph"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p4000009"", ""Name"": "">>"", ""Description"": ""layer name"",        ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Replication"": ""Disabled"",
      ""Description"": ""Alignment parameters"",
      ""X"": 0.0,
      ""Y"": 120.0
    }
  ],
  ""Connectors"": [
    { ""Start"": ""p4000001"", ""End"": ""p4000002"", ""Id"": ""w4001"" },
    { ""Start"": ""p4000007"", ""End"": ""p4000003"", ""Id"": ""w4002"" },
    { ""Start"": ""p4000008"", ""End"": ""p4000004"", ""Id"": ""w4003"" },
    { ""Start"": ""p4000009"", ""End"": ""p4000005"", ""Id"": ""w4004"" }
  ],
  ""Dependencies"": [],
  ""NodeLibraryDependencies"": [],
  ""Thumbnail"": """",
  ""GraphDocumentationURL"": null,
  ""ExtensionWorkspaceData"": [],
  ""Author"": ""Civil3DConnector"",
  ""Linting"": { ""activeLinter"": ""None"", ""activeLinterId"": ""7b75fb44-43fd-4631-a878-29f4d5d8399a"", ""warningCount"": 0, ""errorCount"": 0 },
  ""Bindings"": [],
  ""View"": {
    ""Dynamo"": { ""ScaleFactor"": 1.0, ""HasRunWithoutCrash"": false, ""IsVisibleInDynamoLibrary"": true, ""Version"": ""2.18.0.3593"", ""RunType"": ""Manual"", ""RunPeriod"": ""1000"" },
    ""Camera"": { ""Name"": ""Background Preview"", ""EyeX"": -17.0, ""EyeY"": 24.0, ""EyeZ"": 50.0, ""LookX"": 12.0, ""LookY"": -13.0, ""LookZ"": -58.0, ""UpX"": 0.0, ""UpY"": 1.0, ""UpZ"": 0.0 },
    ""ConnectorPins"": [],
    ""NodeViews"": [
      { ""ShowGeometry"": true, ""Name"": ""Select.SelectObject"", ""Id"": ""n0000004-0000-0000-0000-000000000001"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""Python Script"",        ""Id"": ""n0000004-0000-0000-0000-000000000002"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 400.0, ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""Code Block"",           ""Id"": ""n0000004-0000-0000-0000-000000000003"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 120.0 }
    ],
    ""Annotations"": [],
    ""Notes"": [],
    ""Groups"": []
  }
}";

        // ------------------------------------------------------------------ //
        //  PROFILE FROM SURFACE TEMPLATE
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Samples surface elevations along an alignment and creates a
        /// Civil 3D existing-ground profile (EG profile).
        /// </summary>
        public const string PROFILE_FROM_SURFACE_TEMPLATE = @"{
  ""Uuid"": ""c5000001-0000-0000-0000-000000000005"",
  ""IsCustomNode"": false,
  ""Description"": ""Sample a surface along an alignment to create an EG profile."",
  ""Name"": ""Profile from Surface"",
  ""ElementResolver"": { ""ResolutionMap"": {} },
  ""Inputs"": [],
  ""Outputs"": [],
  ""Nodes"": [
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""Civil3D.Alignment.GetAlignments"",
      ""Id"": ""n0000005-0000-0000-0000-000000000001"",
      ""Inputs"": [],
      ""Outputs"": [{ ""Id"": ""p5000001"", ""Name"": ""alignments"", ""Description"": ""All alignments"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Get all alignments"",
      ""Name"": ""Civil3D.Alignment.GetAlignments"",
      ""X"": 0.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""Civil3D.Surface.GetSurfaces"",
      ""Id"": ""n0000005-0000-0000-0000-000000000002"",
      ""Inputs"": [],
      ""Outputs"": [{ ""Id"": ""p5000002"", ""Name"": ""surfaces"", ""Description"": ""All surfaces"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Disabled"",
      ""Description"": ""Get all surfaces"",
      ""Name"": ""Civil3D.Surface.GetSurfaces"",
      ""X"": 0.0,
      ""Y"": 120.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.ZeroTouch.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""Civil3D.Profile.CreateFromSurface@Civil3D.Alignment,Civil3D.Surface,string,string,string,string"",
      ""Id"": ""n0000005-0000-0000-0000-000000000003"",
      ""Inputs"": [
        { ""Id"": ""p5000003"", ""Name"": ""alignment"",    ""Description"": ""Parent alignment"",  ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000004"", ""Name"": ""surface"",      ""Description"": ""Surface to sample"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000005"", ""Name"": ""profileName"",  ""Description"": ""Profile name"",      ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000006"", ""Name"": ""layer"",        ""Description"": ""Layer name"",         ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000007"", ""Name"": ""styleName"",    ""Description"": ""Profile style"",      ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000008"", ""Name"": ""labelSetName"", ""Description"": ""Label set name"",     ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Outputs"": [{ ""Id"": ""p5000009"", ""Name"": ""profile"", ""Description"": ""Created EG profile"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Auto"",
      ""Description"": ""Create profile from surface"",
      ""Name"": ""Civil3D.Profile.CreateFromSurface"",
      ""X"": 520.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""DSCoreNodesUI.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""List.GetItemAtIndex@var[]..[],int"",
      ""Id"": ""n0000005-0000-0000-0000-000000000004"",
      ""Inputs"": [
        { ""Id"": ""p5000010"", ""Name"": ""list"",  ""Description"": ""List"",  ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000011"", ""Name"": ""index"", ""Description"": ""Index"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Outputs"": [{ ""Id"": ""p5000012"", ""Name"": ""item"", ""Description"": ""Selected alignment"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Auto"",
      ""Description"": ""Pick first alignment"",
      ""Name"": ""List.GetItemAtIndex"",
      ""X"": 260.0,
      ""Y"": 0.0
    },
    {
      ""ConcreteType"": ""DSCoreNodesUI.DSFunction, DynamoCore"",
      ""NodeType"": ""FunctionNode"",
      ""FunctionSignature"": ""List.GetItemAtIndex@var[]..[],int"",
      ""Id"": ""n0000005-0000-0000-0000-000000000005"",
      ""Inputs"": [
        { ""Id"": ""p5000013"", ""Name"": ""list"",  ""Description"": ""List"",  ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000014"", ""Name"": ""index"", ""Description"": ""Index"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Outputs"": [{ ""Id"": ""p5000015"", ""Name"": ""item"", ""Description"": ""Selected surface"", ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }],
      ""Replication"": ""Auto"",
      ""Description"": ""Pick first surface"",
      ""Name"": ""List.GetItemAtIndex"",
      ""X"": 260.0,
      ""Y"": 120.0
    },
    {
      ""ConcreteType"": ""Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore"",
      ""NodeType"": ""CodeBlockNode"",
      ""Code"": ""0;  // alignment index\n0;  // surface index\n\""EG Profile\"";\n\""C-ROAD-PROF\"";  // layer\n\""Existing Ground\"";  // style\n\""Standard\"";"",
      ""Id"": ""n0000005-0000-0000-0000-000000000006"",
      ""Inputs"": [],
      ""Outputs"": [
        { ""Id"": ""p5000016"", ""Name"": "">>"", ""Description"": ""alignment idx"",  ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000017"", ""Name"": "">>"", ""Description"": ""surface idx"",    ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000018"", ""Name"": "">>"", ""Description"": ""profile name"",   ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000019"", ""Name"": "">>"", ""Description"": ""layer"",           ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000020"", ""Name"": "">>"", ""Description"": ""style name"",      ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false },
        { ""Id"": ""p5000021"", ""Name"": "">>"", ""Description"": ""label set name"",  ""UsingDefaultValue"": false, ""Level"": 2, ""UseLevels"": false, ""KeepListStructure"": false }
      ],
      ""Replication"": ""Disabled"",
      ""Description"": ""Profile parameters"",
      ""X"": 0.0,
      ""Y"": 240.0
    }
  ],
  ""Connectors"": [
    { ""Start"": ""p5000001"", ""End"": ""p5000010"", ""Id"": ""w5001"" },
    { ""Start"": ""p5000002"", ""End"": ""p5000013"", ""Id"": ""w5002"" },
    { ""Start"": ""p5000016"", ""End"": ""p5000011"", ""Id"": ""w5003"" },
    { ""Start"": ""p5000017"", ""End"": ""p5000014"", ""Id"": ""w5004"" },
    { ""Start"": ""p5000012"", ""End"": ""p5000003"", ""Id"": ""w5005"" },
    { ""Start"": ""p5000015"", ""End"": ""p5000004"", ""Id"": ""w5006"" },
    { ""Start"": ""p5000018"", ""End"": ""p5000005"", ""Id"": ""w5007"" },
    { ""Start"": ""p5000019"", ""End"": ""p5000006"", ""Id"": ""w5008"" },
    { ""Start"": ""p5000020"", ""End"": ""p5000007"", ""Id"": ""w5009"" },
    { ""Start"": ""p5000021"", ""End"": ""p5000008"", ""Id"": ""w5010"" }
  ],
  ""Dependencies"": [],
  ""NodeLibraryDependencies"": [],
  ""Thumbnail"": """",
  ""GraphDocumentationURL"": null,
  ""ExtensionWorkspaceData"": [],
  ""Author"": ""Civil3DConnector"",
  ""Linting"": { ""activeLinter"": ""None"", ""activeLinterId"": ""7b75fb44-43fd-4631-a878-29f4d5d8399a"", ""warningCount"": 0, ""errorCount"": 0 },
  ""Bindings"": [],
  ""View"": {
    ""Dynamo"": { ""ScaleFactor"": 1.0, ""HasRunWithoutCrash"": false, ""IsVisibleInDynamoLibrary"": true, ""Version"": ""2.18.0.3593"", ""RunType"": ""Manual"", ""RunPeriod"": ""1000"" },
    ""Camera"": { ""Name"": ""Background Preview"", ""EyeX"": -17.0, ""EyeY"": 24.0, ""EyeZ"": 50.0, ""LookX"": 12.0, ""LookY"": -13.0, ""LookZ"": -58.0, ""UpX"": 0.0, ""UpY"": 1.0, ""UpZ"": 0.0 },
    ""ConnectorPins"": [],
    ""NodeViews"": [
      { ""ShowGeometry"": true, ""Name"": ""Civil3D.Alignment.GetAlignments"", ""Id"": ""n0000005-0000-0000-0000-000000000001"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""Civil3D.Surface.GetSurfaces"",      ""Id"": ""n0000005-0000-0000-0000-000000000002"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 120.0 },
      { ""ShowGeometry"": true, ""Name"": ""Civil3D.Profile.CreateFromSurface"",""Id"": ""n0000005-0000-0000-0000-000000000003"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 520.0, ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""List.GetItemAtIndex (alignment)"",  ""Id"": ""n0000005-0000-0000-0000-000000000004"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 260.0, ""Y"": 0.0   },
      { ""ShowGeometry"": true, ""Name"": ""List.GetItemAtIndex (surface)"",    ""Id"": ""n0000005-0000-0000-0000-000000000005"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 260.0, ""Y"": 120.0 },
      { ""ShowGeometry"": true, ""Name"": ""Code Block"",                       ""Id"": ""n0000005-0000-0000-0000-000000000006"", ""IsSetAsInput"": false, ""IsSetAsOutput"": false, ""Excluded"": false, ""X"": 0.0,   ""Y"": 240.0 }
    ],
    ""Annotations"": [],
    ""Notes"": [],
    ""Groups"": []
  }
}";

        // ------------------------------------------------------------------ //
        //  Helper: list all template names
        // ------------------------------------------------------------------ //

        /// <summary>Returns the names of all available template constants.</summary>
        public static string[] AllTemplateNames => new[]
        {
            nameof(CORRIDOR_CREATION_TEMPLATE),
            nameof(PIPE_NETWORK_TEMPLATE),
            nameof(SURFACE_GRADING_TEMPLATE),
            nameof(ALIGNMENT_FROM_POLYLINE_TEMPLATE),
            nameof(PROFILE_FROM_SURFACE_TEMPLATE),
        };

        /// <summary>
        /// Returns the template JSON string for the given template name.
        /// Throws <see cref="ArgumentException"/> for unknown names.
        /// </summary>
        public static string GetTemplate(string templateName)
        {
            return templateName switch
            {
                nameof(CORRIDOR_CREATION_TEMPLATE)       => CORRIDOR_CREATION_TEMPLATE,
                nameof(PIPE_NETWORK_TEMPLATE)            => PIPE_NETWORK_TEMPLATE,
                nameof(SURFACE_GRADING_TEMPLATE)         => SURFACE_GRADING_TEMPLATE,
                nameof(ALIGNMENT_FROM_POLYLINE_TEMPLATE) => ALIGNMENT_FROM_POLYLINE_TEMPLATE,
                nameof(PROFILE_FROM_SURFACE_TEMPLATE)    => PROFILE_FROM_SURFACE_TEMPLATE,
                _ => throw new ArgumentException($"Unknown template name: {templateName}", nameof(templateName))
            };
        }
    }
}
