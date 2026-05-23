using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Newtonsoft.Json;

namespace Civil3DConnector.Integration
{
    /// <summary>
    /// Handles integration with Revit (via IFC/Shared Coordinates) and
    /// Navisworks (via NWC export and clash detection report import).
    /// Civil 3D 2025–2027 compatible.
    /// </summary>
    public class RevitNavisworksConnector : IDisposable
    {
        private readonly Document _doc;
        private readonly Database _db;
        private readonly CivilDocument _civilDoc;
        private bool _disposed;

        public RevitNavisworksConnector(Document document)
        {
            _doc = document ?? throw new ArgumentNullException(nameof(document));
            _db = document.Database;
            _civilDoc = CivilApplication.ActiveDocument;
        }

        // ─────────────────────────────────────────────────────────────────────
        // IFC Export (for Revit BIM integration)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Exports Civil 3D corridors and pipe networks as IFC 2x3/4 for Revit.
        /// Uses simplified geometry export — for full IFC, use AutoCAD's built-in IFC exporter.
        /// </summary>
        public void ExportToIfc(string outputPath, IfcExportOptions options)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ISO-10303-21;");
            sb.AppendLine("HEADER;");
            sb.AppendLine($"FILE_DESCRIPTION(('Civil 3D to IFC Export','Civil3D Intelligent Connector'),'2;1');");
            sb.AppendLine($"FILE_NAME('{Path.GetFileName(outputPath)}','{DateTime.Now:yyyy-MM-ddTHH:mm:ss}',('Civil3D Connector'),('Autodesk Civil 3D'),'{GetCivilVersion()}','','');");
            sb.AppendLine("FILE_SCHEMA(('IFC2X3'));");
            sb.AppendLine("ENDSEC;");
            sb.AppendLine("DATA;");

            int entityId = 1;

            // IFC Project hierarchy
            sb.AppendLine($"#{entityId++}=IFCORGANIZATION($,'Civil3D Connector',$,$,$);");
            sb.AppendLine($"#{entityId++}=IFCPERSON($,'Civil Engineer',$,$,$,$,$,$);");
            sb.AppendLine($"#{entityId++}=IFCPERSONANDORGANIZATION(#{entityId - 2},#{entityId - 1},$);");
            sb.AppendLine($"#{entityId++}=IFCOWNERHISTORY(#{entityId - 1},#{entityId - 3},$,.ADDED.,${DateTime.Now.ToFileTime()},$,${DateTime.Now.ToFileTime()});");

            int ownerHistId = entityId - 1;
            sb.AppendLine($"#{entityId++}=IFCDIMENSIONALEXPONENTS(1,0,0,0,0,0,0);");
            sb.AppendLine($"#{entityId++}=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);");
            int lengthUnitId = entityId - 1;
            sb.AppendLine($"#{entityId++}=IFCUNITASSIGNMENT((#{lengthUnitId}));");
            int unitAssignId = entityId - 1;
            sb.AppendLine($"#{entityId++}=IFCPROJECT('{Guid.NewGuid():N}',#{ownerHistId},'{_civilDoc.Name}',$,$,$,$,$,#{unitAssignId});");
            int projectId = entityId - 1;

            using var tr = _db.TransactionManager.StartOpenCloseTransaction();

            if (options.ExportCorridors)
                ExportCorridorsToIfc(sb, ref entityId, ownerHistId, options, tr);

            if (options.ExportPipeNetworks)
                ExportPipeNetworksToIfc(sb, ref entityId, ownerHistId, options, tr);

            sb.AppendLine("ENDSEC;");
            sb.AppendLine("END-ISO-10303-21;");

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        private void ExportCorridorsToIfc(StringBuilder sb, ref int id, int ownerHistId,
            IfcExportOptions options, Transaction tr)
        {
            var corridorIds = _civilDoc.GetCorridorIds();
            foreach (ObjectId corrId in corridorIds)
            {
                var corridor = tr.GetObject(corrId, OpenMode.ForRead) as Corridor;
                if (corridor == null) continue;

                // Simplified IFC IfcRoad (IFC 4.3) or IfcBuildingElementProxy for IFC 2x3
                sb.AppendLine($"#{id++}=IFCBUILDINGPROXYPROXY('{Guid.NewGuid():N}',#{ownerHistId},'{corridor.Name}','Corridor',$,$,$,$,.NOTDEFINED.);");
            }
        }

        private void ExportPipeNetworksToIfc(StringBuilder sb, ref int id, int ownerHistId,
            IfcExportOptions options, Transaction tr)
        {
            var netIds = _civilDoc.GetPipeNetworkIds();
            foreach (ObjectId netId in netIds)
            {
                var network = tr.GetObject(netId, OpenMode.ForRead) as Network;
                if (network == null) continue;

                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    var pipe = tr.GetObject(pipeId, OpenMode.ForRead) as Pipe;
                    if (pipe == null) continue;

                    var start = pipe.StartPoint;
                    var end = pipe.EndPoint;

                    sb.AppendLine($"#{id++}=IFCCARTESIANPOINT(({start.X:F4},{start.Y:F4},{start.Z:F4}));");
                    int startPtId = id - 1;
                    sb.AppendLine($"#{id++}=IFCCARTESIANPOINT(({end.X:F4},{end.Y:F4},{end.Z:F4}));");
                    int endPtId = id - 1;
                    sb.AppendLine($"#{id++}=IFCPOLYLINE((#{startPtId},#{endPtId}));");
                    int curveId = id - 1;
                    sb.AppendLine($"#{id++}=IFCPIPEFLOWSEGMENT('{Guid.NewGuid():N}',#{ownerHistId},'{network.Name}_{pipe.Name}',$,$,$,$,$,$);");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Navisworks NWC Linkage
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Imports a Navisworks clash detection report (XML/HTML) and annotates
        /// the Civil 3D drawing with clash markers.
        /// </summary>
        public List<ClashMarker> ImportNavisworksClashReport(string reportPath)
        {
            if (!File.Exists(reportPath))
                throw new FileNotFoundException("Navisworks clash report not found.", reportPath);

            var markers = new List<ClashMarker>();

            // Parse XML clash report format (Navisworks Clash Detective export)
            var doc = System.Xml.Linq.XDocument.Load(reportPath);
            var clashes = doc.Descendants("clash");

            foreach (var clash in clashes)
            {
                double x = ParseDouble(clash.Element("clashpoint")?.Element("pos3f")?.Attribute("x")?.Value);
                double y = ParseDouble(clash.Element("clashpoint")?.Element("pos3f")?.Attribute("y")?.Value);
                double z = ParseDouble(clash.Element("clashpoint")?.Element("pos3f")?.Attribute("z")?.Value);

                markers.Add(new ClashMarker
                {
                    ClashId = clash.Attribute("guid")?.Value ?? Guid.NewGuid().ToString(),
                    ClashName = clash.Attribute("name")?.Value ?? "Clash",
                    Position = new Autodesk.AutoCAD.Geometry.Point3d(x, y, z),
                    Distance = ParseDouble(clash.Attribute("dist")?.Value),
                    Status = clash.Attribute("status")?.Value ?? "New",
                    Item1Name = clash.Element("clashobjects")?.Element("clashobject")?.Element("objectattribute")?.Value ?? "",
                    Item2Name = clash.Element("clashobjects")?.Elements("clashobject").Skip(1).FirstOrDefault()?.Element("objectattribute")?.Value ?? ""
                });
            }

            // Draw clash markers in the DWG
            DrawClashMarkers(markers);

            return markers;
        }

        private void DrawClashMarkers(List<ClashMarker> markers)
        {
            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var btr = tr.GetObject(_db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                if (btr == null) return;

                EnsureLayerExists("NAVIS-CLASH", tr);

                foreach (var marker in markers)
                {
                    // Draw a circle at the clash point
                    var circle = new Circle
                    {
                        Center = marker.Position,
                        Radius = 1.0,
                        Layer = "NAVIS-CLASH",
                        ColorIndex = 1 // Red
                    };
                    btr.AppendEntity(circle);
                    tr.AddNewlyCreatedDBObject(circle, true);

                    // Add text label
                    var text = new DBText
                    {
                        Position = marker.Position,
                        TextString = $"CLASH: {marker.ClashName}",
                        Height = 0.5,
                        Layer = "NAVIS-CLASH",
                        ColorIndex = 1
                    };
                    btr.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);
                }

                tr.Commit();
            }
            catch
            {
                tr.Abort();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // InfraWorks Integration
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates an InfraWorks-compatible IMX JSON file from Civil 3D data.
        /// </summary>
        public void ExportToInfraWorksImx(string outputPath)
        {
            using var tr = _db.TransactionManager.StartOpenCloseTransaction();

            var imxData = new
            {
                version = "1.0",
                source = "Civil3DConnector",
                created = DateTime.Now.ToString("O"),
                drawing = _civilDoc.Name,
                alignments = BuildAlignmentsJson(tr),
                surfaces = BuildSurfacesJson(tr),
                pipeNetworks = BuildPipeNetworksJson(tr)
            };

            File.WriteAllText(outputPath, JsonConvert.SerializeObject(imxData, Formatting.Indented), Encoding.UTF8);
        }

        private List<object> BuildAlignmentsJson(Transaction tr)
        {
            var list = new List<object>();
            foreach (ObjectId id in _civilDoc.GetAlignmentIds())
            {
                var align = tr.GetObject(id, OpenMode.ForRead) as Alignment;
                if (align == null) continue;
                list.Add(new
                {
                    name = align.Name,
                    length = align.Length,
                    startStation = align.StartingStation,
                    endStation = align.EndingStation
                });
            }
            return list;
        }

        private List<object> BuildSurfacesJson(Transaction tr)
        {
            var list = new List<object>();
            foreach (ObjectId id in _civilDoc.GetSurfaceIds())
            {
                var surf = tr.GetObject(id, OpenMode.ForRead) as TinSurface;
                if (surf == null) continue;
                list.Add(new
                {
                    name = surf.Name,
                    minElevation = surf.Minimums.MinimumElevation,
                    maxElevation = surf.Maximums.MaximumElevation,
                    vertexCount = surf.Vertices.Count
                });
            }
            return list;
        }

        private List<object> BuildPipeNetworksJson(Transaction tr)
        {
            var list = new List<object>();
            foreach (ObjectId id in _civilDoc.GetPipeNetworkIds())
            {
                var net = tr.GetObject(id, OpenMode.ForRead) as Network;
                if (net == null) continue;
                list.Add(new { name = net.Name, pipeCount = net.GetPipeIds().Count });
            }
            return list;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private string GetCivilVersion()
        {
#if CIVIL3D_2025
            return "Civil 3D 2025";
#elif CIVIL3D_2026
            return "Civil 3D 2026";
#elif CIVIL3D_2027
            return "Civil 3D 2027";
#else
            return "Civil 3D";
#endif
        }

        private static double ParseDouble(string? value)
        {
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double result))
                return result;
            return 0;
        }

        private void EnsureLayerExists(string name, Transaction tr)
        {
            var lt = tr.GetObject(_db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (lt == null || lt.Has(name)) return;
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = name };
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
    // Models
    // ─────────────────────────────────────────────────────────────────────────

    public class IfcExportOptions
    {
        public bool ExportCorridors { get; set; } = true;
        public bool ExportPipeNetworks { get; set; } = true;
        public bool ExportSurfaces { get; set; } = false;
        public string IfcSchemaVersion { get; set; } = "IFC2X3";
        public string CoordinateSystem { get; set; } = "WGS84";
    }

    public class ClashMarker
    {
        public string ClashId { get; set; } = "";
        public string ClashName { get; set; } = "";
        public Autodesk.AutoCAD.Geometry.Point3d Position { get; set; }
        public double Distance { get; set; }
        public string Status { get; set; } = "";
        public string Item1Name { get; set; } = "";
        public string Item2Name { get; set; } = "";
    }
}
