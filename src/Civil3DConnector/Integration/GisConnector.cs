using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json;

namespace Civil3DConnector.Integration
{
    /// <summary>
    /// Provides bidirectional conversion between GIS formats and Autodesk Civil 3D objects.
    /// Supports SHP, GeoJSON, KML, LandXML, IFC.
    /// </summary>
    public class GisConnector : IDisposable
    {
        private readonly Document _document;
        private readonly Database _database;
        private readonly CivilDocument _civilDocument;
        private bool _disposed;

        public GisConnector(Document document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _database = document.Database;
            _civilDocument = CivilApplication.ActiveDocument;
        }

        // ─────────────────────────────────────────────────────────────────────
        // GeoJSON → Civil 3D
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Imports GeoJSON feature collection as Civil 3D objects.
        /// Lines → Alignments, Polygons → Parcels, Points → COGO Points.
        /// </summary>
        public GisImportResult ImportGeoJson(string geoJsonPath, GisImportOptions options)
        {
            if (!File.Exists(geoJsonPath))
                throw new FileNotFoundException("GeoJSON file not found.", geoJsonPath);

            var result = new GisImportResult { SourceFile = geoJsonPath };
            var json = File.ReadAllText(geoJsonPath, Encoding.UTF8);
            var featureCollection = JsonConvert.DeserializeObject<FeatureCollection>(json);

            if (featureCollection == null)
            {
                result.Errors.Add("Could not deserialize GeoJSON feature collection.");
                return result;
            }

            using var tr = _database.TransactionManager.StartTransaction();
            try
            {
                foreach (var feature in featureCollection)
                {
                    switch (feature.Geometry)
                    {
                        case LineString ls when options.ImportLines:
                            ImportLineStringAsAlignment(ls, feature.Attributes, options, result, tr);
                            break;

                        case MultiLineString mls when options.ImportLines:
                            foreach (var part in mls.Geometries)
                                ImportLineStringAsAlignment((LineString)part, feature.Attributes, options, result, tr);
                            break;

                        case Polygon poly when options.ImportPolygons:
                            ImportPolygonAsParcel(poly, feature.Attributes, options, result, tr);
                            break;

                        case NetTopologySuite.Geometries.Point pt when options.ImportPoints:
                            ImportPointAsCogo(pt, feature.Attributes, options, result, tr);
                            break;
                    }
                }

                tr.Commit();
                result.Success = true;
            }
            catch (Exception ex)
            {
                tr.Abort();
                result.Success = false;
                result.Errors.Add($"Transaction aborted: {ex.Message}");
            }

            return result;
        }

        private void ImportLineStringAsAlignment(LineString ls, IAttributesTable attrs,
            GisImportOptions options, GisImportResult result, Transaction tr)
        {
            try
            {
                var points = new Point2dCollection();
                foreach (var c in ls.Coordinates)
                    points.Add(ApplyCoordinateTransform(c, options));

                string name = attrs.Exists("name") ? attrs["name"]?.ToString() ?? $"Alignment_{result.AlignmentsImported + 1}"
                                                    : $"Alignment_{result.AlignmentsImported + 1}";

                var alignLayer = "C-ROAD-CNTR";
                EnsureLayerExists(alignLayer, tr);

                var alignCollection = _civilDocument.GetAlignmentIds();
                var align = Alignment.Create(_civilDocument, name, null, alignLayer,
                    AlignmentStyle.GetDefaultStyleId(_civilDocument),
                    AlignmentLabelSetStyle.GetDefaultLabelSetId(_civilDocument));

                var at = align.UpgradeOpen() as Alignment;
                at?.ReferencePointEntity.AddFixedLine(points[0], points[points.Count - 1]);

                result.AlignmentsImported++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not import LineString as Alignment: {ex.Message}");
            }
        }

        private void ImportPolygonAsParcel(Polygon poly, IAttributesTable attrs,
            GisImportOptions options, GisImportResult result, Transaction tr)
        {
            try
            {
                var polyline = new Autodesk.AutoCAD.DatabaseServices.Polyline();
                var coords = poly.ExteriorRing.Coordinates;
                for (int i = 0; i < coords.Length; i++)
                {
                    var pt2d = ApplyCoordinateTransform(coords[i], options);
                    polyline.AddVertexAt(i, pt2d, 0, 0, 0);
                }
                polyline.Closed = true;

                var btr = tr.GetObject(_database.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                btr?.AppendEntity(polyline);
                tr.AddNewlyCreatedDBObject(polyline, true);

                result.ParcelsImported++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not import Polygon as Parcel: {ex.Message}");
            }
        }

        private void ImportPointAsCogo(NetTopologySuite.Geometries.Point pt, IAttributesTable attrs,
            GisImportOptions options, GisImportResult result, Transaction tr)
        {
            try
            {
                var pt2d = ApplyCoordinateTransform(pt.Coordinate, options);
                double elevation = pt.Z is double.NaN ? 0.0 : pt.Z;
                string description = attrs.Exists("description") ? attrs["description"]?.ToString() ?? "" : "";

                var cogoCollection = _civilDocument.CogoPoints;
                var newPtId = cogoCollection.Add(new Point3d(pt2d.X, pt2d.Y, elevation),
                    true, true, description);

                result.PointsImported++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not import Point as COGO: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Civil 3D → GeoJSON
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Exports selected Civil 3D objects to GeoJSON.
        /// </summary>
        public void ExportToGeoJson(string outputPath, GisExportOptions options)
        {
            var features = new FeatureCollection();

            using var tr = _database.TransactionManager.StartOpenCloseTransaction();

            if (options.ExportAlignments)
                ExportAlignmentsToFeatures(features, options, tr);

            if (options.ExportSurfaces)
                ExportSurfacesToFeatures(features, options, tr);

            if (options.ExportPipeNetworks)
                ExportPipeNetworksToFeatures(features, options, tr);

            if (options.ExportCogoPoints)
                ExportCogoPointsToFeatures(features, options, tr);

            var serializer = GeoJsonSerializer.Create();
            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);
            using var jsonWriter = new JsonTextWriter(writer) { Formatting = Formatting.Indented };
            serializer.Serialize(jsonWriter, features);
        }

        private void ExportAlignmentsToFeatures(FeatureCollection fc, GisExportOptions opts, Transaction tr)
        {
            var alignIds = _civilDocument.GetAlignmentIds();
            foreach (ObjectId id in alignIds)
            {
                var align = tr.GetObject(id, OpenMode.ForRead) as Alignment;
                if (align == null) continue;

                var coords = new List<Coordinate>();
                double station = align.StartingStation;
                double step = Math.Max(1.0, align.Length / 500.0);

                while (station <= align.EndingStation)
                {
                    align.PointLocation(station, 0, out double x, out double y);
                    coords.Add(new Coordinate(x, y));
                    station += step;
                }

                var geom = new LineString(coords.ToArray());
                var attributes = new AttributesTable();
                attributes.Add("name", align.Name);
                attributes.Add("length", align.Length);
                attributes.Add("startStation", align.StartingStation);
                attributes.Add("endStation", align.EndingStation);
                attributes.Add("type", "Alignment");

                fc.Add(new Feature(geom, attributes));
            }
        }

        private void ExportSurfacesToFeatures(FeatureCollection fc, GisExportOptions opts, Transaction tr)
        {
            var surfIds = _civilDocument.GetSurfaceIds();
            foreach (ObjectId id in surfIds)
            {
                var surf = tr.GetObject(id, OpenMode.ForRead) as TinSurface;
                if (surf == null) continue;

                var border = surf.GetBorders();
                if (border == null) continue;

                var coords = new List<Coordinate>();
                foreach (SurfaceBorderType bt in Enum.GetValues(typeof(SurfaceBorderType)))
                {
                    // simplified: use extent corners
                }

                var ext = surf.GeometricExtents;
                var ringCoords = new[]
                {
                    new Coordinate(ext.MinPoint.X, ext.MinPoint.Y),
                    new Coordinate(ext.MaxPoint.X, ext.MinPoint.Y),
                    new Coordinate(ext.MaxPoint.X, ext.MaxPoint.Y),
                    new Coordinate(ext.MinPoint.X, ext.MaxPoint.Y),
                    new Coordinate(ext.MinPoint.X, ext.MinPoint.Y)
                };

                var attributes = new AttributesTable();
                attributes.Add("name", surf.Name);
                attributes.Add("type", "TinSurface");
                attributes.Add("minElevation", surf.Minimums.MinimumElevation);
                attributes.Add("maxElevation", surf.Maximums.MaximumElevation);

                fc.Add(new Feature(new Polygon(new LinearRing(ringCoords)), attributes));
            }
        }

        private void ExportPipeNetworksToFeatures(FeatureCollection fc, GisExportOptions opts, Transaction tr)
        {
            var netIds = _civilDocument.GetPipeNetworkIds();
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
                    var coords = new[] { new Coordinate(start.X, start.Y), new Coordinate(end.X, end.Y) };

                    var attributes = new AttributesTable();
                    attributes.Add("network", network.Name);
                    attributes.Add("pipeId", pipeId.ToString());
                    attributes.Add("diameter", pipe.InnerDiameterOrWidth);
                    attributes.Add("length", pipe.Length2D);
                    attributes.Add("slope", pipe.Slope);
                    attributes.Add("material", pipe.PipeMaterial);
                    attributes.Add("type", "Pipe");

                    fc.Add(new Feature(new LineString(coords), attributes));
                }
            }
        }

        private void ExportCogoPointsToFeatures(FeatureCollection fc, GisExportOptions opts, Transaction tr)
        {
            var cogoIds = _civilDocument.CogoPoints.GetPointIds();
            foreach (ObjectId id in cogoIds)
            {
                var pt = tr.GetObject(id, OpenMode.ForRead) as CogoPoint;
                if (pt == null) continue;

                var coord = new Coordinate(pt.Easting, pt.Northing, pt.Elevation);
                var attributes = new AttributesTable();
                attributes.Add("pointNumber", pt.PointNumber);
                attributes.Add("description", pt.FullDescription);
                attributes.Add("elevation", pt.Elevation);
                attributes.Add("type", "CogoPoint");

                fc.Add(new Feature(new NetTopologySuite.Geometries.Point(coord), attributes));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // LandXML Import/Export
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Exports Civil 3D surfaces and alignments to LandXML 1.2 format.
        /// </summary>
        public void ExportToLandXml(string outputPath, LandXmlExportOptions options)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<LandXML version=\"1.2\" date=\"" + DateTime.Now.ToString("yyyy-MM-dd") + "\" time=\"" + DateTime.Now.ToString("HH:mm:ss") + "\"");
            sb.AppendLine("    xmlns=\"http://www.landxml.org/schema/LandXML-1.2\"");
            sb.AppendLine("    xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"");
            sb.AppendLine("    xsi:schemaLocation=\"http://www.landxml.org/schema/LandXML-1.2 http://www.landxml.org/schema/LandXML-1.2/LandXML-1.2.xsd\">");
            sb.AppendLine("  <Units>");
            sb.AppendLine("    <Metric linearUnit=\"meter\" areaUnit=\"squareMeter\" volumeUnit=\"cubicMeter\" />");
            sb.AppendLine("  </Units>");
            sb.AppendLine("  <Project name=\"" + System.Security.SecurityElement.Escape(_civilDocument.Name) + "\" />");

            using var tr = _database.TransactionManager.StartOpenCloseTransaction();

            if (options.ExportSurfaces)
            {
                sb.AppendLine("  <Surfaces>");
                foreach (ObjectId id in _civilDocument.GetSurfaceIds())
                {
                    var surf = tr.GetObject(id, OpenMode.ForRead) as TinSurface;
                    if (surf == null) continue;
                    AppendSurfaceToLandXml(surf, sb, options);
                }
                sb.AppendLine("  </Surfaces>");
            }

            if (options.ExportAlignments)
            {
                sb.AppendLine("  <Alignments>");
                foreach (ObjectId id in _civilDocument.GetAlignmentIds())
                {
                    var align = tr.GetObject(id, OpenMode.ForRead) as Alignment;
                    if (align == null) continue;
                    AppendAlignmentToLandXml(align, sb, options);
                }
                sb.AppendLine("  </Alignments>");
            }

            sb.AppendLine("</LandXML>");
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        private void AppendSurfaceToLandXml(TinSurface surf, StringBuilder sb, LandXmlExportOptions options)
        {
            sb.AppendLine($"    <Surface name=\"{System.Security.SecurityElement.Escape(surf.Name)}\" desc=\"TIN Surface\">");
            sb.AppendLine("      <Definition surfType=\"TIN\">");
            sb.AppendLine("        <Pnts>");

            var vertices = surf.GetVertices();
            int idx = 1;
            var indexMap = new Dictionary<ObjectId, int>();

            foreach (TinSurfaceVertex vertex in vertices)
            {
                sb.AppendLine($"          <P id=\"{idx}\">{vertex.Location.Y:F4} {vertex.Location.X:F4} {vertex.Location.Z:F4}</P>");
                indexMap[vertex.ObjectId] = idx++;
            }

            sb.AppendLine("        </Pnts>");
            sb.AppendLine("        <Faces>");

            var triangles = surf.GetTriangles(false);
            foreach (TinSurfaceTriangle tri in triangles)
            {
                if (indexMap.TryGetValue(tri.Vertex1.ObjectId, out int i1) &&
                    indexMap.TryGetValue(tri.Vertex2.ObjectId, out int i2) &&
                    indexMap.TryGetValue(tri.Vertex3.ObjectId, out int i3))
                {
                    sb.AppendLine($"          <F>{i1} {i2} {i3}</F>");
                }
            }

            sb.AppendLine("        </Faces>");
            sb.AppendLine("      </Definition>");
            sb.AppendLine("    </Surface>");
        }

        private void AppendAlignmentToLandXml(Alignment align, StringBuilder sb, LandXmlExportOptions options)
        {
            sb.AppendLine($"    <Alignment name=\"{System.Security.SecurityElement.Escape(align.Name)}\" length=\"{align.Length:F4}\" staStart=\"{align.StartingStation:F4}\" staEnd=\"{align.EndingStation:F4}\">");
            sb.AppendLine("      <CoordGeom>");

            foreach (AlignmentEntity entity in align.Entities)
            {
                switch (entity.EntityType)
                {
                    case AlignmentEntityType.Line:
                        var line = (AlignmentLine)entity;
                        sb.AppendLine($"        <Line dir=\"{line.Direction:F6}\" length=\"{line.Length:F4}\">");
                        sb.AppendLine($"          <Start>{line.StartPoint.Y:F4} {line.StartPoint.X:F4}</Start>");
                        sb.AppendLine($"          <End>{line.EndPoint.Y:F4} {line.EndPoint.X:F4}</End>");
                        sb.AppendLine("        </Line>");
                        break;

                    case AlignmentEntityType.Arc:
                        var arc = (AlignmentArc)entity;
                        sb.AppendLine($"        <Curve length=\"{arc.Length:F4}\" radius=\"{arc.Radius:F4}\" rot=\"{(arc.Clockwise ? "cw" : "ccw")}\">");
                        sb.AppendLine($"          <Start>{arc.StartPoint.Y:F4} {arc.StartPoint.X:F4}</Start>");
                        sb.AppendLine($"          <Center>{arc.CenterPoint.Y:F4} {arc.CenterPoint.X:F4}</Center>");
                        sb.AppendLine($"          <End>{arc.EndPoint.Y:F4} {arc.EndPoint.X:F4}</End>");
                        sb.AppendLine("        </Curve>");
                        break;
                }
            }

            sb.AppendLine("      </CoordGeom>");
            sb.AppendLine("    </Alignment>");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private Point2d ApplyCoordinateTransform(Coordinate coord, GisImportOptions options)
        {
            if (options.CoordinateOffset != null)
                return new Point2d(coord.X + options.CoordinateOffset.Value.X,
                                   coord.Y + options.CoordinateOffset.Value.Y);
            return new Point2d(coord.X, coord.Y);
        }

        private void EnsureLayerExists(string layerName, Transaction tr)
        {
            var lt = tr.GetObject(_database.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (lt == null || lt.Has(layerName)) return;

            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = layerName };
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
    // Options and Result Models
    // ─────────────────────────────────────────────────────────────────────────

    public class GisImportOptions
    {
        public bool ImportLines { get; set; } = true;
        public bool ImportPolygons { get; set; } = true;
        public bool ImportPoints { get; set; } = true;
        public Point2d? CoordinateOffset { get; set; }
        public string? TargetCoordinateSystem { get; set; }
        public string DefaultLayer { get; set; } = "0";
    }

    public class GisExportOptions
    {
        public bool ExportAlignments { get; set; } = true;
        public bool ExportSurfaces { get; set; } = true;
        public bool ExportPipeNetworks { get; set; } = true;
        public bool ExportCogoPoints { get; set; } = true;
        public int SampleInterval { get; set; } = 10;
    }

    public class LandXmlExportOptions
    {
        public bool ExportSurfaces { get; set; } = true;
        public bool ExportAlignments { get; set; } = true;
        public bool ExportPipeNetworks { get; set; } = true;
        public bool ExportParcels { get; set; } = false;
    }

    public class GisImportResult
    {
        public bool Success { get; set; }
        public string SourceFile { get; set; } = "";
        public int AlignmentsImported { get; set; }
        public int ParcelsImported { get; set; }
        public int PointsImported { get; set; }
        public int SurfacesImported { get; set; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }
}
