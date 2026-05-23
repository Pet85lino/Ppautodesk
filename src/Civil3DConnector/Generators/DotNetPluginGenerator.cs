// DotNetPluginGenerator.cs
// Generates complete, compilable C# Visual Studio project trees for Civil 3D
// .NET plugins.  Each generated project is a standalone directory containing:
//   - A .csproj (SDK-style, targeting net48)
//   - Commands.cs          — [CommandMethod] decorated entry points
//   - PalettePanel.cs      — WPF UserControl hosted in a PaletteSet
//   - RibbonCommands.cs    — IExtensionApplication that adds ribbon items
//   - EventHandlers.cs     — DocumentCreated / ObjectModified handlers
//   - CivilOperations.cs   — Transaction-wrapped Civil 3D object operations
//
// All generated code:
//   - Compiles against Civil 3D 2025 ObjectARX / AeccDB DLLs (hint paths
//     replace the DLL directory tokens at generation time).
//   - Uses the Autodesk.Civil.ApplicationServices.CivilApplication entry
//     point rather than the obsolete CivilizationDocument pattern.
//   - Follows [assembly: CommandClass(...)] registration so AutoCAD can
//     discover commands without reflection scanning the entire assembly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Civil3DConnector.Generators
{
    /// <summary>
    /// Options that control what code sections are generated.
    /// </summary>
    public sealed class PluginGenerationOptions
    {
        /// <summary>Root namespace for all generated files.</summary>
        public string Namespace          { get; set; } = "MyCompany.Civil3DPlugin";

        /// <summary>Assembly / project name.</summary>
        public string AssemblyName       { get; set; } = "Civil3DPlugin";

        /// <summary>Human-readable plugin title (used in ribbon / palette captions).</summary>
        public string PluginTitle        { get; set; } = "Civil 3D Plugin";

        /// <summary>
        /// Directory containing the Civil 3D ObjectARX DLLs.
        /// Default matches Civil 3D 2025 x64 default install path.
        /// </summary>
        public string AcadDllDirectory   { get; set; } =
            @"C:\Program Files\Autodesk\AutoCAD 2025";

        /// <summary>
        /// Directory containing AeccDbMgd.dll and related Civil 3D managed DLLs.
        /// </summary>
        public string Civil3DDllDirectory { get; set; } =
            @"C:\Program Files\Autodesk\AutoCAD 2025\ACA";

        /// <summary>Include a WPF PaletteSet panel.</summary>
        public bool IncludePalette       { get; set; } = true;

        /// <summary>Include Ribbon API integration.</summary>
        public bool IncludeRibbon        { get; set; } = true;

        /// <summary>Include document/object event handlers.</summary>
        public bool IncludeEventHandlers { get; set; } = true;

        /// <summary>
        /// List of (CommandName, MethodName, Description) for the Commands.cs file.
        /// Defaults to a representative set if empty.
        /// </summary>
        public List<(string cmdName, string methodName, string description)> Commands { get; set; } = new();
    }

    /// <summary>
    /// Represents one generated source file (relative path + content).
    /// </summary>
    public sealed class GeneratedFile
    {
        /// <summary>Path relative to the output directory root.</summary>
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>File contents.</summary>
        public string Content      { get; set; } = string.Empty;
    }

    /// <summary>
    /// Generates a complete Civil 3D .NET plugin project as a collection of
    /// <see cref="GeneratedFile"/> objects that can be written to disk.
    /// </summary>
    public sealed class DotNetPluginGenerator
    {
        private PluginGenerationOptions _opts;

        // ------------------------------------------------------------------ //
        //  Entry points
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Generate a full plugin project using the supplied options.
        /// Returns every file that should be written to disk.
        /// </summary>
        public IReadOnlyList<GeneratedFile> GenerateProject(PluginGenerationOptions options)
        {
            _opts = options ?? throw new ArgumentNullException(nameof(options));

            // Populate default commands if none specified
            if (_opts.Commands.Count == 0)
                _opts.Commands = DefaultCommands();

            var files = new List<GeneratedFile>
            {
                new() { RelativePath = $"{_opts.AssemblyName}.csproj", Content = GenerateCsproj() },
                new() { RelativePath = "Commands.cs",                  Content = GenerateCommands() },
                new() { RelativePath = "CivilOperations.cs",           Content = GenerateCivilOperations() },
            };

            if (_opts.IncludePalette)
            {
                files.Add(new() { RelativePath = "PalettePanel.xaml",    Content = GeneratePaletteXaml() });
                files.Add(new() { RelativePath = "PalettePanel.xaml.cs", Content = GeneratePaletteCSharp() });
                files.Add(new() { RelativePath = "PaletteHost.cs",       Content = GeneratePaletteHost() });
            }
            if (_opts.IncludeRibbon)
                files.Add(new() { RelativePath = "RibbonCommands.cs",    Content = GenerateRibbonCommands() });

            if (_opts.IncludeEventHandlers)
                files.Add(new() { RelativePath = "EventHandlers.cs",     Content = GenerateEventHandlers() });

            return files;
        }

        /// <summary>
        /// Write a generated project to <paramref name="outputDirectory"/>,
        /// creating subdirectories as required.
        /// </summary>
        public void WriteProjectToDisk(PluginGenerationOptions options, string outputDirectory)
        {
            var files = GenerateProject(options);
            foreach (var file in files)
            {
                var fullPath = Path.Combine(outputDirectory, file.RelativePath);
                var dir      = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
                File.WriteAllText(fullPath, file.Content, Encoding.UTF8);
            }
        }

        // ------------------------------------------------------------------ //
        //  .csproj
        // ------------------------------------------------------------------ //

        private string GenerateCsproj()
        {
            var sb = new StringBuilder();
            sb.AppendLine(@"<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <LangVersion>10</LangVersion>
    <Optimize>true</Optimize>
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
            sb.AppendLine($"    <AssemblyName>{_opts.AssemblyName}</AssemblyName>");
            sb.AppendLine($"    <RootNamespace>{_opts.Namespace}</RootNamespace>");
            sb.AppendLine(@"  </PropertyGroup>

  <!-- ================================================================ -->
  <!--  AutoCAD / Civil 3D references  (adjust paths to match install)  -->
  <!-- ================================================================ -->
  <ItemGroup>");

            var acadRefs = new[]
            {
                ("acmgd",                 "acmgd.dll"),
                ("acdbmgd",               "acdbmgd.dll"),
                ("accoremgd",             "accoremgd.dll"),
                ("AcWindows",             "AcWindows.dll"),
                ("AdWindows",             "AdWindows.dll"),
                ("AdIntPolyMgd",          "AdIntPolyMgd.dll"),
                ("AutoCAD.ApplicationServices.Core", "AutoCAD.ApplicationServices.Core.dll"),
            };

            foreach (var (hintName, dll) in acadRefs)
            {
                sb.AppendLine($@"    <Reference Include=""{hintName}"">
      <HintPath>{_opts.AcadDllDirectory}\{dll}</HintPath>
      <Private>false</Private>
    </Reference>");
            }

            var civilRefs = new[]
            {
                ("AeccDbMgd",  "AeccDbMgd.dll"),
                ("AecBaseMgd", "AecBaseMgd.dll"),
                ("AeccXUiPipe", "AeccXUiPipe.dll"),
            };

            foreach (var (hintName, dll) in civilRefs)
            {
                sb.AppendLine($@"    <Reference Include=""{hintName}"">
      <HintPath>{_opts.Civil3DDllDirectory}\{dll}</HintPath>
      <Private>false</Private>
    </Reference>");
            }

            sb.AppendLine(@"  </ItemGroup>

</Project>");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ //
        //  Commands.cs
        // ------------------------------------------------------------------ //

        private string GenerateCommands()
        {
            var sb = new StringBuilder();
            sb.AppendLine($@"// Commands.cs — AutoCAD command entry points for {_opts.PluginTitle}
// Each [CommandMethod] attribute registers the command in the AutoCAD command
// table.  The CommandFlags.Modal flag is used so commands run on the main
// thread and can safely access the drawing database.

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using System;
using System.Linq;

[assembly: CommandClass(typeof({_opts.Namespace}.Commands))]

namespace {_opts.Namespace}
{{
    /// <summary>
    /// AutoCAD command methods for {_opts.PluginTitle}.
    /// </summary>
    public sealed class Commands
    {{");

            foreach (var (cmdName, methodName, description) in _opts.Commands)
            {
                sb.AppendLine($@"
        /// <summary>{description}</summary>
        [CommandMethod(""{cmdName}"", CommandFlags.Modal)]
        public void {methodName}()
        {{
            var doc    = Application.DocumentManager.MdiActiveDocument;
            var editor = doc.Editor;
            try
            {{
                editor.WriteMessage($""\n[{_opts.PluginTitle}] Running {cmdName}...\n"");
                using var tr = doc.Database.TransactionManager.StartTransaction();
                // --- TODO: implement {methodName} body here ---
                // CivilOperations provides helper methods for Civil 3D objects.
                var civilDoc = CivilApplication.ActiveDocument;
                editor.WriteMessage($""\n[{_opts.PluginTitle}] {cmdName} completed.\n"");
                tr.Commit();
            }}
            catch (Exception ex)
            {{
                editor.WriteMessage($""\n[ERROR] {cmdName}: {{ex.Message}}\n"");
            }}
        }}");
            }

            sb.AppendLine(@"    }
}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ //
        //  CivilOperations.cs
        // ------------------------------------------------------------------ //

        private string GenerateCivilOperations()
        {
            return $@"// CivilOperations.cs — Transaction-wrapped Civil 3D operations for {_opts.PluginTitle}
// Every public method opens its own transaction so callers don't need to
// manage transaction state, unless they need to chain operations in the same
// undo group — in that case pass an existing Transaction parameter.

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;

namespace {_opts.Namespace}
{{
    /// <summary>
    /// Helper class containing Civil 3D object operations with built-in
    /// transaction management.
    /// </summary>
    public static class CivilOperations
    {{
        // ================================================================ //
        //  Document accessors
        // ================================================================ //

        private static Document AcadDoc
            => Application.DocumentManager.MdiActiveDocument;

        private static Database AcadDb => AcadDoc.Database;

        private static CivilDocument CivilDoc
            => CivilApplication.ActiveDocument;

        // ================================================================ //
        //  Alignment operations
        // ================================================================ //

        /// <summary>
        /// Returns the names of all alignments in the active document.
        /// </summary>
        public static IReadOnlyList<string> GetAlignmentNames()
        {{
            var names = new List<string>();
            using var tr = AcadDb.TransactionManager.StartOpenCloseTransaction();
            foreach (var id in CivilDoc.GetAlignmentIds())
            {{
                var al = (Alignment)tr.GetObject(id, OpenMode.ForRead);
                names.Add(al.Name);
            }}
            return names;
        }}

        /// <summary>
        /// Returns the ObjectId of the alignment with the given name,
        /// or ObjectId.Null if not found.
        /// </summary>
        public static ObjectId FindAlignmentByName(string name)
        {{
            using var tr = AcadDb.TransactionManager.StartOpenCloseTransaction();
            foreach (var id in CivilDoc.GetAlignmentIds())
            {{
                var al = (Alignment)tr.GetObject(id, OpenMode.ForRead);
                if (string.Equals(al.Name, name, StringComparison.OrdinalIgnoreCase))
                    return id;
            }}
            return ObjectId.Null;
        }}

        /// <summary>
        /// Samples elevations from <paramref name="surfaceName"/> along
        /// <paramref name="alignmentName"/> at <paramref name="interval"/> spacing.
        /// Returns a list of (station, elevation) tuples.
        /// </summary>
        public static IReadOnlyList<(double Station, double Elevation)> SampleSurfaceAlongAlignment(
            string alignmentName,
            string surfaceName,
            double interval = 5.0)
        {{
            var samples = new List<(double, double)>();
            using var tr = AcadDb.TransactionManager.StartOpenCloseTransaction();

            var alId = FindAlignmentByName(alignmentName);
            if (alId.IsNull) throw new InvalidOperationException($""Alignment '{{alignmentName}}' not found."");

            ObjectId surfId = ObjectId.Null;
            foreach (var id in CivilDoc.GetSurfaceIds())
            {{
                var s = (TinSurface)tr.GetObject(id, OpenMode.ForRead);
                if (string.Equals(s.Name, surfaceName, StringComparison.OrdinalIgnoreCase))
                {{ surfId = id; break; }}
            }}
            if (surfId.IsNull) throw new InvalidOperationException($""Surface '{{surfaceName}}' not found."");

            var al  = (Alignment)tr.GetObject(alId,  OpenMode.ForRead);
            var tin = (TinSurface)tr.GetObject(surfId, OpenMode.ForRead);

            double sta = al.StartStation;
            while (sta <= al.EndStation + 1e-6)
            {{
                try
                {{
                    var pt = al.GetPointAtDist(sta - al.StartStation);
                    var z  = tin.FindElevationAtXY(pt.X, pt.Y);
                    samples.Add((Math.Round(sta, 3), Math.Round(z, 3)));
                }}
                catch {{ /* skip missing data */ }}
                sta += interval;
            }}
            return samples;
        }}

        // ================================================================ //
        //  Surface operations
        // ================================================================ //

        /// <summary>
        /// Returns summary statistics for the named TIN surface.
        /// </summary>
        public static SurfaceStatistics GetSurfaceStatistics(string surfaceName)
        {{
            using var tr = AcadDb.TransactionManager.StartOpenCloseTransaction();
            foreach (var id in CivilDoc.GetSurfaceIds())
            {{
                var s = (TinSurface)tr.GetObject(id, OpenMode.ForRead);
                if (!string.Equals(s.Name, surfaceName, StringComparison.OrdinalIgnoreCase)) continue;
                var props = s.GetGeneralProperties();
                return new SurfaceStatistics
                {{
                    Name       = s.Name,
                    MinElev    = Math.Round(props.MinimumElevation, 3),
                    MaxElev    = Math.Round(props.MaximumElevation, 3),
                    MeanElev   = Math.Round(props.MeanElevation,    3),
                    PointCount = props.NumberOfPoints,
                    TriangleCount = props.NumberOfTriangles,
                    Area2D     = Math.Round(props.SurfaceArea2D,    3),
                    Area3D     = Math.Round(props.SurfaceArea3D,    3),
                }};
            }}
            throw new InvalidOperationException($""Surface '{{surfaceName}}' not found."");
        }}

        // ================================================================ //
        //  Corridor operations
        // ================================================================ //

        /// <summary>
        /// Rebuilds the named corridor and returns the elapsed time in ms.
        /// </summary>
        public static double RebuildCorridor(string corridorName)
        {{
            using var _ = AcadDoc.LockDocument();
            using var tr = AcadDb.TransactionManager.StartTransaction();
            foreach (var id in CivilDoc.GetCorridorIds())
            {{
                var corr = (Corridor)tr.GetObject(id, OpenMode.ForWrite);
                if (!string.Equals(corr.Name, corridorName, StringComparison.OrdinalIgnoreCase)) continue;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                corr.Rebuild();
                sw.Stop();
                tr.Commit();
                return sw.Elapsed.TotalMilliseconds;
            }}
            throw new InvalidOperationException($""Corridor '{{corridorName}}' not found."");
        }}

        // ================================================================ //
        //  Pipe network operations
        // ================================================================ //

        /// <summary>
        /// Returns a list of pipe records for all pipes in the named network.
        /// </summary>
        public static IReadOnlyList<PipeRecord> GetPipesInNetwork(string networkName)
        {{
            var records = new List<PipeRecord>();
            using var tr = AcadDb.TransactionManager.StartOpenCloseTransaction();
            foreach (var netId in CivilDoc.GetNetworkIds())
            {{
                var net = (Network)tr.GetObject(netId, OpenMode.ForRead);
                if (!string.Equals(net.Name, networkName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var pipeId in net.GetPipeIds())
                {{
                    try
                    {{
                        var pipe = (Pipe)tr.GetObject(pipeId, OpenMode.ForRead);
                        records.Add(new PipeRecord
                        {{
                            Name         = pipe.Name,
                            DiameterMm   = Math.Round(pipe.InnerDiameterOrWidth * 1000, 1),
                            LengthM      = Math.Round(pipe.Length2D, 3),
                            SlopePct     = Math.Round(pipe.Slope * 100, 4),
                            InvertStartM = Math.Round(pipe.StartPoint.Z, 3),
                            InvertEndM   = Math.Round(pipe.EndPoint.Z, 3),
                        }});
                    }}
                    catch {{ /* skip malformed pipes */ }}
                }}
                break;
            }}
            return records;
        }}

        // ================================================================ //
        //  Profile operations
        // ================================================================ //

        /// <summary>
        /// Creates an existing-ground (EG) profile by sampling
        /// <paramref name="surfaceName"/> along <paramref name="alignmentName"/>
        /// and returns the ObjectId of the new profile.
        /// </summary>
        public static ObjectId CreateEGProfileFromSurface(
            string alignmentName,
            string surfaceName,
            string profileName = ""EG Profile"",
            string profileLayer = ""C-ROAD-PROF"",
            string profileStyleName = ""Existing Ground"",
            string labelSetName = ""Standard"")
        {{
            using var docLock = AcadDoc.LockDocument();
            using var tr = AcadDb.TransactionManager.StartTransaction();

            var alId   = FindAlignmentByName(alignmentName);
            if (alId.IsNull) throw new InvalidOperationException($""Alignment '{{alignmentName}}' not found."");

            ObjectId surfId = ObjectId.Null;
            foreach (var id in CivilDoc.GetSurfaceIds())
            {{
                var s = (TinSurface)tr.GetObject(id, OpenMode.ForRead);
                if (string.Equals(s.Name, surfaceName, StringComparison.OrdinalIgnoreCase))
                {{ surfId = id; break; }}
            }}
            if (surfId.IsNull) throw new InvalidOperationException($""Surface '{{surfaceName}}' not found."");

            var profileId = Profile.CreateFromSurface(
                profileName, alId, surfId,
                profileLayer, profileStyleName, labelSetName);

            tr.Commit();
            return profileId;
        }}
    }}

    // ================================================================ //
    //  Data transfer objects
    // ================================================================ //

    /// <summary>Surface statistics summary.</summary>
    public sealed class SurfaceStatistics
    {{
        public string Name          {{ get; set; }} = string.Empty;
        public double MinElev       {{ get; set; }}
        public double MaxElev       {{ get; set; }}
        public double MeanElev      {{ get; set; }}
        public int    PointCount    {{ get; set; }}
        public int    TriangleCount {{ get; set; }}
        public double Area2D        {{ get; set; }}
        public double Area3D        {{ get; set; }}
    }}

    /// <summary>Pipe record for inventory / reporting.</summary>
    public sealed class PipeRecord
    {{
        public string Name         {{ get; set; }} = string.Empty;
        public double DiameterMm   {{ get; set; }}
        public double LengthM      {{ get; set; }}
        public double SlopePct     {{ get; set; }}
        public double InvertStartM {{ get; set; }}
        public double InvertEndM   {{ get; set; }}
    }}
}}
";
        }

        // ------------------------------------------------------------------ //
        //  PalettePanel.xaml
        // ------------------------------------------------------------------ //

        private string GeneratePaletteXaml()
        {
            return $@"<!-- PalettePanel.xaml — WPF user control hosted inside a Civil 3D PaletteSet -->
<UserControl x:Class=""{_opts.Namespace}.PalettePanel""
             xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
             xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
             xmlns:mc=""http://schemas.openxmlformats.org/markup-compatibility/2006""
             xmlns:d=""http://schemas.microsoft.com/expression/blend/2008""
             mc:Ignorable=""d""
             d:DesignHeight=""600"" d:DesignWidth=""300""
             Background=""#FF2D2D30"" Foreground=""White"">
  <Grid Margin=""8"">
    <Grid.RowDefinitions>
      <RowDefinition Height=""Auto""/>
      <RowDefinition Height=""Auto""/>
      <RowDefinition Height=""*""/>
      <RowDefinition Height=""Auto""/>
    </Grid.RowDefinitions>

    <!-- Header -->
    <TextBlock Grid.Row=""0"" Text=""{_opts.PluginTitle}""
               FontSize=""14"" FontWeight=""Bold"" Margin=""0,0,0,8""
               Foreground=""#FF00AAFF""/>

    <!-- Section: Alignments -->
    <GroupBox Grid.Row=""1"" Header=""Alignments"" Margin=""0,0,0,8""
              Foreground=""#FFDDDDDD"" BorderBrush=""#FF444444"">
      <StackPanel Margin=""4"">
        <Button x:Name=""btnListAlignments""  Content=""List Alignments""
                Click=""BtnListAlignments_Click"" Margin=""0,2"" Height=""26""/>
        <Button x:Name=""btnSampleSurface""   Content=""Sample Surface Along Alignment""
                Click=""BtnSampleSurface_Click"" Margin=""0,2"" Height=""26""/>
      </StackPanel>
    </GroupBox>

    <!-- Output list -->
    <ScrollViewer Grid.Row=""2"" VerticalScrollBarVisibility=""Auto"">
      <ListBox x:Name=""lstOutput""
               Background=""#FF1E1E1E"" Foreground=""#FFDDDDDD""
               BorderBrush=""#FF444444"" FontFamily=""Consolas"" FontSize=""11""/>
    </ScrollViewer>

    <!-- Status bar -->
    <TextBlock Grid.Row=""3"" x:Name=""txtStatus""
               Text=""Ready"" Foreground=""#FF888888""
               Margin=""0,4,0,0"" FontSize=""10""/>
  </Grid>
</UserControl>
";
        }

        // ------------------------------------------------------------------ //
        //  PalettePanel.xaml.cs
        // ------------------------------------------------------------------ //

        private string GeneratePaletteCSharp()
        {
            return $@"// PalettePanel.xaml.cs — Code-behind for the WPF palette panel
using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Windows.Controls;

namespace {_opts.Namespace}
{{
    /// <summary>
    /// Interaction logic for PalettePanel.xaml.
    /// The panel is hosted inside a PaletteSet managed by <see cref=""PaletteHost""/>.
    /// </summary>
    public partial class PalettePanel : UserControl
    {{
        public PalettePanel()
        {{
            InitializeComponent();
        }}

        // ---------------------------------------------------------------- //
        //  Button handlers
        // ---------------------------------------------------------------- //

        private void BtnListAlignments_Click(object sender, System.Windows.RoutedEventArgs e)
        {{
            RunSafe(() =>
            {{
                var names = CivilOperations.GetAlignmentNames();
                lstOutput.Items.Clear();
                if (names.Count == 0)
                {{
                    lstOutput.Items.Add(""No alignments found in drawing."");
                    return;
                }}
                foreach (var name in names)
                    lstOutput.Items.Add(name);
                SetStatus($""Found {{names.Count}} alignment(s)."");
            }});
        }}

        private void BtnSampleSurface_Click(object sender, System.Windows.RoutedEventArgs e)
        {{
            RunSafe(() =>
            {{
                // Prompt user for names via quick-picks (simplified: hard-coded for demo)
                var doc    = Application.DocumentManager.MdiActiveDocument;
                var editor = doc.Editor;

                var alRes = editor.GetString(""\nAlignment name: "");
                if (alRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
                var sfRes = editor.GetString(""\nSurface name: "");
                if (sfRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;

                var samples = CivilOperations.SampleSurfaceAlongAlignment(
                    alRes.StringResult, sfRes.StringResult, interval: 10.0);

                lstOutput.Items.Clear();
                foreach (var (sta, elev) in samples)
                    lstOutput.Items.Add($""{{sta,10:F3}}  {{elev,10:F3}}"");
                SetStatus($""{{samples.Count}} samples returned."");
            }});
        }}

        // ---------------------------------------------------------------- //
        //  Helpers
        // ---------------------------------------------------------------- //

        private void RunSafe(Action action)
        {{
            try
            {{
                action();
            }}
            catch (Exception ex)
            {{
                lstOutput.Items.Add($""[ERROR] {{ex.Message}}"");
                SetStatus(""Error — see output list."");
            }}
        }}

        private void SetStatus(string message)
        {{
            txtStatus.Text = message;
        }}
    }}
}}
";
        }

        // ------------------------------------------------------------------ //
        //  PaletteHost.cs
        // ------------------------------------------------------------------ //

        private string GeneratePaletteHost()
        {
            return $@"// PaletteHost.cs — Creates and manages the AutoCAD PaletteSet that hosts the WPF panel.
// The PaletteSet is a singleton: calling ShowPalette() multiple times toggles
// visibility rather than creating duplicate palette windows.

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using System;

namespace {_opts.Namespace}
{{
    /// <summary>
    /// Manages the lifecycle of the <see cref=""{_opts.PluginTitle}""/> PaletteSet.
    /// </summary>
    public static class PaletteHost
    {{
        private static PaletteSet? _paletteSet;
        private static readonly Guid _paletteSetGuid = Guid.Parse(""a1b2c3d4-e5f6-7890-abcd-ef1234567890"");

        /// <summary>
        /// Show (or bring to front) the palette.
        /// Creates the PaletteSet on first call.
        /// </summary>
        public static void ShowPalette()
        {{
            if (_paletteSet == null)
            {{
                _paletteSet = new PaletteSet(""{_opts.PluginTitle}"", _paletteSetGuid)
                {{
                    Style = PaletteSetStyles.ShowAutoHideButton
                           | PaletteSetStyles.ShowPropertiesMenu
                           | PaletteSetStyles.Snappable,
                    MinimumSize = new System.Drawing.Size(280, 300),
                    DockEnabled = DockSides.Left | DockSides.Right,
                }};
                var panel = new PalettePanel();
                _paletteSet.Add(""{_opts.PluginTitle}"", new System.Windows.Forms.Integration.ElementHost
                {{
                    Child = panel,
                    Dock  = System.Windows.Forms.DockStyle.Fill,
                }});
            }}

            _paletteSet.Visible = !_paletteSet.Visible;
            if (_paletteSet.Visible)
                _paletteSet.Activate(0);
        }}

        /// <summary>Dispose the PaletteSet (call from IExtensionApplication.Terminate).</summary>
        public static void DisposePalette()
        {{
            _paletteSet?.Dispose();
            _paletteSet = null;
        }}
    }}
}}
";
        }

        // ------------------------------------------------------------------ //
        //  RibbonCommands.cs
        // ------------------------------------------------------------------ //

        private string GenerateRibbonCommands()
        {
            var sb = new StringBuilder();
            sb.AppendLine($@"// RibbonCommands.cs — Adds a ribbon tab and panel for {_opts.PluginTitle}.
// Implements IExtensionApplication so the ribbon items are created at startup
// (InitializeExtension) and removed at shutdown (TerminateExtension).
//
// Ribbon API: Autodesk.Windows (AdWindows.dll) — available in AutoCAD 2015+

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using System;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.IO;

[assembly: ExtensionApplication(typeof({_opts.Namespace}.RibbonApplication))]

namespace {_opts.Namespace}
{{
    /// <summary>
    /// IExtensionApplication entry point — adds ribbon UI on startup.
    /// </summary>
    public sealed class RibbonApplication : IExtensionApplication
    {{
        private const string TabId   = ""{_opts.AssemblyName}_Tab"";
        private const string PanelId = ""{_opts.AssemblyName}_Panel"";

        public void Initialize()
        {{
            // Ribbon may not be available yet if loading from acad.exe args.
            // Subscribe to IDLE to defer ribbon creation safely.
            Application.Idle += OnIdle;
        }}

        public void Terminate()
        {{
            Application.Idle -= OnIdle;
            if (_opts_IncludesPalette()) PaletteHost.DisposePalette();
            RemoveRibbonTab();
        }}

        private static bool _opts_IncludesPalette() => {(_opts.IncludePalette ? "true" : "false")};

        private bool _ribbonCreated = false;
        private void OnIdle(object? sender, EventArgs e)
        {{
            if (_ribbonCreated) return;
            if (ComponentManager.Ribbon == null) return;
            Application.Idle -= OnIdle;
            CreateRibbonTab();
            _ribbonCreated = true;
        }}

        // ---------------------------------------------------------------- //
        //  Ribbon construction
        // ---------------------------------------------------------------- //

        private void CreateRibbonTab()
        {{
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;

            // Remove any stale tab from a previous session
            RemoveRibbonTab();

            var tab = new RibbonTab
            {{
                Id    = TabId,
                Title = ""{_opts.PluginTitle}"",
                IsContextualTab = false,
            }};
            ribbon.Tabs.Add(tab);

            var panel = new RibbonPanel
            {{
                Source = new RibbonPanelSource
                {{
                    Id    = PanelId,
                    Title = ""Tools"",
                }},
            }};
            tab.Panels.Add(panel);");

            foreach (var (cmdName, methodName, description) in _opts.Commands)
            {
                sb.AppendLine($@"
            panel.Source.Items.Add(MakeButton(""{cmdName}"", ""{description}"", ""_{cmdName}""));");
            }

            if (_opts.IncludePalette)
            {
                sb.AppendLine(@"
            panel.Source.Items.Add(new RibbonSeparator());
            panel.Source.Items.Add(MakeButton(""SHOWPALETTE"", ""Toggle Palette"", ""_SHOWPALETTE""));");
            }

            sb.AppendLine(@"
            ribbon.Refresh();
        }

        private void RemoveRibbonTab()
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null) return;
            var existing = ribbon.FindTab(TabId);
            if (existing != null) ribbon.Tabs.Remove(existing);
        }

        private static RibbonButton MakeButton(string id, string label, string command)
        {
            return new RibbonButton
            {
                Id              = id,
                Text            = label,
                ShowText        = true,
                ShowImage       = false,
                CommandHandler  = new RibbonCommandHandler(command),
                Size            = RibbonItemSize.Large,
                Orientation     = System.Windows.Controls.Orientation.Vertical,
                ToolTip         = new RibbonToolTip { Title = label },
            };
        }
    }

    /// <summary>Simple ICommand that posts an AutoCAD command string.</summary>
    internal sealed class RibbonCommandHandler : System.Windows.Input.ICommand
    {
        private readonly string _command;
        public RibbonCommandHandler(string command) => _command = command;

        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.SendStringToExecute(_command + ""\n"", true, false, false);
        }
    }
}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ //
        //  EventHandlers.cs
        // ------------------------------------------------------------------ //

        private string GenerateEventHandlers()
        {
            return $@"// EventHandlers.cs — AutoCAD / Civil 3D event subscriptions for {_opts.PluginTitle}
// Subscribe to events in Commands or IExtensionApplication.Initialize();
// always unsubscribe in Terminate() to avoid memory leaks.

using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;

namespace {_opts.Namespace}
{{
    /// <summary>
    /// Manages event subscriptions for the plugin lifecycle.
    /// </summary>
    public sealed class EventHandlers : IDisposable
    {{
        private readonly DocumentCollection _docMgr;
        private bool _disposed;

        public EventHandlers()
        {{
            _docMgr = Application.DocumentManager;
            _docMgr.DocumentCreated   += OnDocumentCreated;
            _docMgr.DocumentToBeDestroyed += OnDocumentDestroyed;
            // Subscribe to existing open documents
            foreach (Document doc in _docMgr)
                AttachDocumentEvents(doc);
        }}

        // ---------------------------------------------------------------- //
        //  DocumentCollection events
        // ---------------------------------------------------------------- //

        private void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
        {{
            try
            {{
                AttachDocumentEvents(e.Document);
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($""\n[{_opts.PluginTitle}] Document created: {{e.Document.Name}}\n"");
            }}
            catch (Exception ex)
            {{
                LogError(nameof(OnDocumentCreated), ex);
            }}
        }}

        private void OnDocumentDestroyed(object? sender, DocumentCollectionEventArgs e)
        {{
            try
            {{
                DetachDocumentEvents(e.Document);
            }}
            catch (Exception ex)
            {{
                LogError(nameof(OnDocumentDestroyed), ex);
            }}
        }}

        // ---------------------------------------------------------------- //
        //  Per-document events
        // ---------------------------------------------------------------- //

        private void AttachDocumentEvents(Document doc)
        {{
            doc.Database.ObjectModified += OnObjectModified;
            doc.Database.ObjectAppended += OnObjectAppended;
        }}

        private void DetachDocumentEvents(Document doc)
        {{
            doc.Database.ObjectModified -= OnObjectModified;
            doc.Database.ObjectAppended -= OnObjectAppended;
        }}

        private void OnObjectModified(object? sender, ObjectEventArgs e)
        {{
            try
            {{
                // Only respond to Civil 3D alignment, surface, corridor, or pipe changes
                using var tr = e.DBObject.Database.TransactionManager.StartOpenCloseTransaction();
                var obj = tr.GetObject(e.DBObject.ObjectId, OpenMode.ForRead);
                if (obj is Alignment al)
                {{
                    OnAlignmentModified(al);
                }}
                else if (obj is TinSurface surf)
                {{
                    OnSurfaceModified(surf);
                }}
                else if (obj is Corridor corr)
                {{
                    OnCorridorModified(corr);
                }}
                else if (obj is Pipe pipe)
                {{
                    OnPipeModified(pipe);
                }}
            }}
            catch (Exception ex) when (IsExpectedModifyException(ex))
            {{
                // Suppress expected transient errors (e.g. objects being deleted)
            }}
        }}

        private void OnObjectAppended(object? sender, ObjectEventArgs e)
        {{
            // Called when a new object is added to the database.
            // Can be used to enforce naming standards or log new objects.
        }}

        // ---------------------------------------------------------------- //
        //  Type-specific handlers — override with project-specific logic
        // ---------------------------------------------------------------- //

        protected virtual void OnAlignmentModified(Alignment alignment)
        {{
            // Example: log alignment modification
            // Application.DocumentManager.MdiActiveDocument?.Editor
            //     .WriteMessage($""\n[{_opts.PluginTitle}] Alignment modified: {{alignment.Name}}\n"");
        }}

        protected virtual void OnSurfaceModified(TinSurface surface)
        {{
            // Example: trigger downstream rebuild
        }}

        protected virtual void OnCorridorModified(Corridor corridor)
        {{
            // Example: update feature line labels
        }}

        protected virtual void OnPipeModified(Pipe pipe)
        {{
            // Example: validate slope
        }}

        // ---------------------------------------------------------------- //
        //  IDisposable
        // ---------------------------------------------------------------- //

        public void Dispose()
        {{
            if (_disposed) return;
            _disposed = true;
            _docMgr.DocumentCreated       -= OnDocumentCreated;
            _docMgr.DocumentToBeDestroyed -= OnDocumentDestroyed;
            foreach (Document doc in _docMgr)
                DetachDocumentEvents(doc);
        }}

        // ---------------------------------------------------------------- //
        //  Helpers
        // ---------------------------------------------------------------- //

        private static void LogError(string context, Exception ex)
        {{
            try
            {{
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($""\n[ERROR][{_opts.PluginTitle}][{{context}}] {{ex.Message}}\n"");
            }}
            catch {{ /* never throw from error handler */ }}
        }}

        private static bool IsExpectedModifyException(Exception ex)
            => ex is Autodesk.AutoCAD.Runtime.Exception ace &&
               ace.ErrorStatus is
                   Autodesk.AutoCAD.Runtime.ErrorStatus.eWasErased or
                   Autodesk.AutoCAD.Runtime.ErrorStatus.eNullObjectId;
    }}
}}
";
        }

        // ------------------------------------------------------------------ //
        //  Private helpers
        // ------------------------------------------------------------------ //

        private static List<(string, string, string)> DefaultCommands() => new()
        {
            ("C3DLISTALIGNMENTS",  "ListAlignments",  "List all alignments in the drawing"),
            ("C3DLISTSURFACES",    "ListSurfaces",    "List all surfaces in the drawing"),
            ("C3DSAMPLESURFACE",   "SampleSurface",   "Sample surface elevations along an alignment"),
            ("C3DREBUILDCORRIDOR", "RebuildCorridor", "Rebuild a Civil 3D corridor"),
            ("C3DPIPEINVENTORY",   "PipeInventory",   "Output a pipe inventory report"),
            ("C3DSHOWPALETTE",     "ShowPalette",     "Toggle the plugin palette panel"),
        };
    }
}
