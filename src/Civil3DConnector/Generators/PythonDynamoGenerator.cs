// PythonDynamoGenerator.cs
// Generates Python 3 (CPython) scripts intended to run inside Dynamo Python
// Script nodes embedded in Civil 3D 2025-2027.
//
// Naming convention for returned strings:
//   - Single-line string literals use regular C# strings.
//   - Multi-line code bodies are raw verbatim strings so indentation is exact.
//
// Every generated script:
//   1. Opens with clr.AddReference() for all required Autodesk assemblies.
//   2. Handles both IronPython 2 (legacy) and CPython 3 (Dynamo 2.13+) via a
//      try/except compatibility guard where the APIs differ.
//   3. Uses doc.LockDocument() + TransactionManager for all write operations.
//   4. Returns results via the OUT variable and errors via an 'errors' list so
//      the Dynamo graph can surface problems without crashing.

using System;
using System.Collections.Generic;
using System.Text;

namespace Civil3DConnector.Generators
{
    /// <summary>
    /// Generates Python 3 script strings for use inside Dynamo Python Script
    /// nodes that interact with the Civil 3D API.
    /// </summary>
    public static class PythonDynamoGenerator
    {
        // ================================================================== //
        //  Surface operations
        // ================================================================== //

        /// <summary>
        /// Returns a Python script that samples a TIN surface along an alignment
        /// at a user-specified station interval and returns station/elevation pairs.
        /// </summary>
        public static string BuildSurfaceSamplerScript() => Preamble() + @"
# ---- Inputs ----
alignment = IN[0]   # Civil3D Alignment wrapper
surface   = IN[1]   # Civil3D TinSurface wrapper
interval  = IN[2]   # station sampling interval (drawing units)

doc = Application.DocumentManager.MdiActiveDocument
db  = doc.Database

def sample_surface_along_alignment(alignment, surface, interval):
    rows   = []
    errors = []
    try:
        al_obj  = alignment.InternalDBObject
        tin_obj = surface.InternalDBObject

        start_sta = al_obj.StartStation
        end_sta   = al_obj.EndStation

        station = start_sta
        while station <= end_sta + 1e-6:
            try:
                pt = al_obj.GetPointAtDist(station - start_sta)
                z  = tin_obj.FindElevationAtXY(pt.X, pt.Y)
                rows.append({
                    'station':   round(station, 3),
                    'x':         round(pt.X, 3),
                    'y':         round(pt.Y, 3),
                    'elevation': round(z, 3),
                })
            except Exception as e:
                errors.append(f'Station {station:.3f}: {e}')
            station += interval
        # Always include the end station
        if abs(station - interval - end_sta) > 1e-6:
            try:
                pt = al_obj.GetPointAtDist(end_sta - start_sta)
                z  = tin_obj.FindElevationAtXY(pt.X, pt.Y)
                rows.append({
                    'station':   round(end_sta, 3),
                    'x':         round(pt.X, 3),
                    'y':         round(pt.Y, 3),
                    'elevation': round(z, 3),
                })
            except Exception as e:
                errors.append(f'End station: {e}')
    except Exception as ex:
        errors.append(str(ex))
    return {'rows': rows, 'errors': errors}

OUT = sample_surface_along_alignment(alignment, surface, float(interval))
";

        /// <summary>
        /// Returns a Python script that computes cut/fill volume between two
        /// TIN surfaces using the Civil 3D TinVolumeSurface API and returns a
        /// summary dict including net, cut, and fill volumes.
        /// </summary>
        public static string BuildCutFillVolumeScript() => Preamble() + @"
import clr
clr.AddReference('AeccDbMgd')
from Autodesk.Civil.DatabaseServices import TinVolumeSurface
from Autodesk.AutoCAD.DatabaseServices import OpenMode

# ---- Inputs ----
existing_surface = IN[0]   # existing TIN surface
proposed_surface = IN[1]   # proposed TIN surface
volume_name      = IN[2]   # name for the new volume surface (str)

doc = Application.DocumentManager.MdiActiveDocument
db  = doc.Database

def compute_volumes(existing, proposed, vol_name):
    result = {'cut': 0.0, 'fill': 0.0, 'net': 0.0, 'errors': []}
    try:
        with doc.LockDocument():
            with db.TransactionManager.StartTransaction() as tr:
                vol_id = TinVolumeSurface.Create(db, vol_name)
                vol    = tr.GetObject(vol_id, OpenMode.ForWrite)
                vol.SetBaseAndComparisonSurfaces(
                    existing.InternalObjectId,
                    proposed.InternalObjectId
                )
                vol.Rebuild()
                props = vol.GetVolumeProperties()
                result['cut']  = round(props.CutVolume,  3)
                result['fill'] = round(props.FillVolume, 3)
                result['net']  = round(props.NetVolume,  3)
                tr.Commit()
    except Exception as ex:
        result['errors'].append(str(ex))
    return result

OUT = compute_volumes(existing_surface, proposed_surface, str(volume_name))
";

        /// <summary>
        /// Returns a Python script that extracts contour polylines at a user-
        /// specified elevation from a TIN surface and returns their 3D coordinates.
        /// </summary>
        public static string BuildContourExtractionScript() => Preamble() + @"
# ---- Inputs ----
surface   = IN[0]   # Civil3D TinSurface
elevation = IN[1]   # contour elevation
interval  = IN[2]   # minor contour interval (for batch mode; 0 = single elevation)

doc = Application.DocumentManager.MdiActiveDocument
db  = doc.Database

def extract_contours(surface, elevation, interval):
    result = {'contours': [], 'errors': []}
    try:
        tin = surface.InternalDBObject
        ext = tin.GeometricExtents

        # Use Civil 3D surface contour extraction
        # ContourSet is available through the surface's contour collection
        elevations = []
        if interval and float(interval) > 0:
            z = ext.MinPoint.Z
            while z <= ext.MaxPoint.Z + 1e-6:
                elevations.append(z)
                z += float(interval)
        else:
            elevations = [float(elevation)]

        for z_val in elevations:
            try:
                # GetContoursAtElevation returns a list of Point3dCollection
                contour_set = tin.GetContoursAtElevation(z_val)
                for contour in contour_set:
                    pts = [{'x': round(p.X, 3), 'y': round(p.Y, 3), 'z': round(p.Z, 3)}
                           for p in contour]
                    result['contours'].append({'elevation': z_val, 'points': pts})
            except Exception as ce:
                result['errors'].append(f'Elevation {z_val}: {ce}')
    except Exception as ex:
        result['errors'].append(str(ex))
    return result

OUT = extract_contours(surface, elevation, interval)
";

        /// <summary>
        /// Returns a Python script that adds break-line data points to an
        /// existing TIN surface from a list of Dynamo Point objects.
        /// </summary>
        public static string BuildSurfaceBreaklineScript() => Preamble() + @"
from Autodesk.AutoCAD.Geometry import Point3dCollection, Point3d
from Autodesk.AutoCAD.DatabaseServices import OpenMode

# ---- Inputs ----
surface     = IN[0]   # Civil3D TinSurface
points      = IN[1]   # list of Dynamo Point objects (the breakline vertices)
breakline_description = IN[2]   # string description

doc = Application.DocumentManager.MdiActiveDocument
db  = doc.Database

def add_breakline(surface, points, description):
    result = {'added': False, 'errors': []}
    try:
        with doc.LockDocument():
            with db.TransactionManager.StartTransaction() as tr:
                tin = tr.GetObject(surface.InternalObjectId, OpenMode.ForWrite)
                pt3d_col = Point3dCollection()
                for pt in points:
                    pt3d_col.Add(Point3d(pt.X, pt.Y, pt.Z))
                from Autodesk.Civil.DatabaseServices import BreaklineType
                tin.BreaklinesDefinition.AddStandardBreakline(
                    pt3d_col, str(description), BreaklineType.Standard
                )
                tin.Rebuild()
                tr.Commit()
                result['added'] = True
    except Exception as ex:
        result['errors'].append(str(ex))
    return result

OUT = add_breakline(surface, points, breakline_description)
";

        // ================================================================== //
        //  Alignment geometry extraction
        // ================================================================== //

        /// <summary>
        /// Returns a Python script that extracts tangent and curve geometric
        /// data from an alignment and returns it as a list of dicts.
        /// </summary>
        public static string BuildAlignmentGeometryScript() => Preamble() + @"
from Autodesk.Civil.DatabaseServices import AlignmentEntityType

# ---- Inputs ----
alignment = IN[0]   # Civil3D Alignment

def extract_alignment_geometry(alignment):
    result = {'entities': [], 'total_length': 0.0, 'errors': []}
    try:
        al = alignment.InternalDBObject
        result['total_length'] = round(al.Length, 3)

        for i in range(al.Entities.Count):
            entity = al.Entities[i]
            ent_data = {
                'index':  i,
                'type':   str(entity.EntityType),
                'length': round(entity.Length, 3),
            }
            try:
                if entity.EntityType == AlignmentEntityType.Line:
                    ent_data.update({
                        'start_x': round(entity.StartPoint.X, 3),
                        'start_y': round(entity.StartPoint.Y, 3),
                        'end_x':   round(entity.EndPoint.X, 3),
                        'end_y':   round(entity.EndPoint.Y, 3),
                        'bearing': round(entity.Direction, 6),
                    })
                elif entity.EntityType in (AlignmentEntityType.Arc,
                                           AlignmentEntityType.SpiralCurveSpiral,
                                           AlignmentEntityType.SpiralCurve,
                                           AlignmentEntityType.CurveSpiral):
                    ent_data.update({
                        'radius':       round(entity.Radius, 3),
                        'delta_angle':  round(entity.Delta, 6),
                        'center_x':     round(entity.CenterPoint.X, 3),
                        'center_y':     round(entity.CenterPoint.Y, 3),
                        'chord_length': round(entity.Chord, 3),
                        'direction':    'CW' if entity.Clockwise else 'CCW',
                    })
            except Exception as fe:
                ent_data['field_error'] = str(fe)
            result['entities'].append(ent_data)
    except Exception as ex:
        result['errors'].append(str(ex))
    return result

OUT = extract_alignment_geometry(alignment)
";

        // ================================================================== //
        //  Profile data manipulation
        // ================================================================== //

        /// <summary>
        /// Returns a Python script that extracts station/elevation PVI data
        /// from a profile and also interpolates elevations at user stations.
        /// </summary>
        public static string BuildProfileDataScript() => Preamble() + @"
from Autodesk.Civil.DatabaseServices import Profile

# ---- Inputs ----
profile          = IN[0]   # Civil3D Profile
query_stations   = IN[1]   # list of stations to query (can be empty list)

def extract_profile_data(profile, query_stations):
    result = {'pvis': [], 'queried': [], 'errors': []}
    try:
        prof = profile.InternalDBObject

        # --- PVI table ---
        for i in range(prof.PVIs.Count):
            pvi = prof.PVIs[i]
            result['pvis'].append({
                'index':   i,
                'station': round(pvi.Station, 3),
                'elevation': round(pvi.Elevation, 3),
            })

        # --- Interpolated queries ---
        for sta in (query_stations or []):
            try:
                elev = prof.ElevationAt(float(sta))
                result['queried'].append({'station': round(float(sta), 3), 'elevation': round(elev, 3)})
            except Exception as qe:
                result['queried'].append({'station': float(sta), 'error': str(qe)})
    except Exception as ex:
        result['errors'].append(str(ex))
    return result

OUT = extract_profile_data(profile, query_stations)
";

        /// <summary>
        /// Returns a Python script that inserts or updates a PVI in a profile
        /// at a given station and elevation.
        /// </summary>
        public static string BuildProfilePviEditScript() => Preamble() + @"
from Autodesk.AutoCAD.DatabaseServices import OpenMode

# ---- Inputs ----
profile   = IN[0]   # Civil3D Profile
station   = IN[1]   # PVI station
elevation = IN[2]   # PVI elevation

doc = Application.DocumentManager.MdiActiveDocument
db  = doc.Database

def upsert_pvi(profile, station, elevation):
    result = {'success': False, 'errors': []}
    try:
        with doc.LockDocument():
            with db.TransactionManager.StartTransaction() as tr:
                prof = tr.GetObject(profile.InternalObjectId, OpenMode.ForWrite)
                sta  = float(station)
                elev = float(elevation)
                # Check if a PVI already exists at this station (within tolerance)
                tol = 0.001
                existing_pvi = None
                for i in range(prof.PVIs.Count):
                    pvi = prof.PVIs[i]
                    if abs(pvi.Station - sta) < tol:
                        existing_pvi = pvi
                        break
                if existing_pvi is not None:
                    existing_pvi.Elevation = elev
                else:
                    prof.PVIs.AddPVI(sta, elev)
                tr.Commit()
                result['success'] = True
    except Exception as ex:
        result['errors'].append(str(ex))
    return result

OUT = upsert_pvi(profile, station, elevation)
";

        // ================================================================== //
        //  Corridor section extraction
        // ================================================================== //

        /// <summary>
        /// Returns a Python script that iterates corridor baselines and extracts
        /// cross-section point data (offset/elevation) at given stations.
        /// </summary>
        public static string BuildCorridorSectionExtractorScript() => Preamble() + @"
from Autodesk.Civil.DatabaseServices import (
    Corridor, CorridorFeatureLine, CorridorFeatureLineCollection
)

# ---- Inputs ----
corridor       = IN[0]   # Civil3D Corridor
start_station  = IN[1]   # start station
end_station    = IN[2]   # end station
interval       = IN[3]   # station interval

def extract_sections(corridor, start_sta, end_sta, interval):
    result = {'sections': [], 'errors': []}
    try:
        corr = corridor.InternalDBObject
        start_sta = float(start_sta)
        end_sta   = float(end_sta)
        interval  = float(interval)

        for baseline in corr.Baselines:
            bl_name = str(baseline.Name)
            station = start_sta
            while station <= end_sta + 1e-6:
                sec_data = {
                    'baseline': bl_name,
                    'station':  round(station, 3),
                    'points':   [],
                }
                try:
                    # CorridorSection at this station
                    corr_section = baseline.GetCorridorSectionAtStation(station)
                    for link in corr_section.Links:
                        for pt in link.Points:
                            sec_data['points'].append({
                                'offset':    round(pt.Offset, 4),
                                'elevation': round(pt.Elevation, 4),
                                'code':      str(pt.Code) if pt.Code else '',
                            })
                except Exception as se:
                    sec_data['error'] = str(se)
                result['sections'].append(sec_data)
                station += interval

    except Exception as ex:
        result['errors'].append(str(ex))
    return result

OUT = extract_sections(corridor, start_station, end_station, interval)
";

        // ================================================================== //
        //  Pipe network analysis
        // ================================================================== //

        /// <summary>
        /// Returns a Python script that collects all pipes from all networks
        /// and returns a tabular list of key properties.
        /// </summary>
        public static string BuildPipeInventoryScript() => Preamble() + @"
from Autodesk.Civil.DatabaseServices import Network, Pipe

# ---- Inputs ----
networks = IN[0]   # list of Civil3D PipeNetwork wrappers

doc = Application.DocumentManager.MdiActiveDocument
db  = doc.Database

def pipe_inventory(networks):
    rows   = []
    errors = []
    try:
        net_list = networks if hasattr(networks, '__iter__') else [networks]
        for net_wrapper in net_list:
            try:
                net = net_wrapper.InternalDBObject
                net_name = str(net.Name)
                for pipe_id in net.GetPipeIds():
                    try:
                        with db.TransactionManager.StartTransaction() as tr:
                            pipe = tr.GetObject(pipe_id, OpenMode.ForRead)
                            rows.append({
                                'network':         net_name,
                                'name':            str(pipe.Name),
                                'diameter_mm':     round(pipe.InnerDiameterOrWidth * 1000, 1),
                                'length_m':        round(pipe.Length2D, 3),
                                'slope_pct':       round(pipe.Slope * 100, 4),
                                'invert_start_m':  round(pipe.StartPoint.Z, 3),
                                'invert_end_m':    round(pipe.EndPoint.Z, 3),
                                'material':        str(pipe.PartFamilyName) if hasattr(pipe, 'PartFamilyName') else '',
                                'description':     str(pipe.Description) if pipe.Description else '',
                            })
                    except Exception as pe:
                        errors.append(f'Pipe {pipe_id}: {pe}')
            except Exception as ne:
                errors.append(f'Network: {ne}')
    except Exception as ex:
        errors.append(str(ex))
    return {'rows': rows, 'errors': errors}

OUT = pipe_inventory(networks)
";

        /// <summary>
        /// Returns a Python script that checks each pipe's hydraulic gradient
        /// and flags any that fail a minimum-velocity or minimum-slope check.
        /// </summary>
        public static string BuildPipeHydraulicCheckScript() => Preamble() + @"
from Autodesk.Civil.DatabaseServices import Network, Pipe
from Autodesk.AutoCAD.DatabaseServices import OpenMode
import math

# ---- Inputs ----
networks    = IN[0]   # list of network wrappers
min_slope   = IN[1]   # minimum acceptable slope (e.g. 0.005 = 0.5%)
min_vel_mps = IN[2]   # minimum velocity m/s (e.g. 0.6)

doc = Application.DocumentManager.MdiActiveDocument
db  = doc.Database

def manning_velocity(diameter_m, slope, n=0.013):
    # Full-flow Manning's equation: V = (1/n) * R^(2/3) * S^(1/2)
    # For a full circular pipe: R = D/4
    R = diameter_m / 4.0
    if R <= 0 or slope <= 0:
        return 0.0
    return (1.0 / n) * (R ** (2.0 / 3.0)) * (slope ** 0.5)

def hydraulic_check(networks, min_slope, min_vel):
    issues = []
    ok     = []
    errors = []
    min_slope = float(min_slope)
    min_vel   = float(min_vel)
    net_list  = networks if hasattr(networks, '__iter__') else [networks]

    for net_wrapper in net_list:
        try:
            net      = net_wrapper.InternalDBObject
            net_name = str(net.Name)
            for pipe_id in net.GetPipeIds():
                try:
                    with db.TransactionManager.StartTransaction() as tr:
                        pipe      = tr.GetObject(pipe_id, OpenMode.ForRead)
                        slope     = abs(pipe.Slope)
                        dia_m     = pipe.InnerDiameterOrWidth
                        velocity  = manning_velocity(dia_m, slope)
                        pipe_name = str(pipe.Name)
                        failed    = []
                        if slope < min_slope:
                            failed.append(f'slope {slope*100:.3f}% < {min_slope*100:.3f}%')
                        if velocity < min_vel:
                            failed.append(f'velocity {velocity:.3f} m/s < {min_vel} m/s')
                        record = {
                            'network':  net_name,
                            'pipe':     pipe_name,
                            'slope':    round(slope, 6),
                            'vel_mps':  round(velocity, 3),
                        }
                        if failed:
                            record['failures'] = failed
                            issues.append(record)
                        else:
                            ok.append(record)
                except Exception as pe:
                    errors.append(f'Pipe {pipe_id}: {pe}')
        except Exception as ne:
            errors.append(f'Network: {ne}')
    return {'issues': issues, 'ok': ok, 'errors': errors}

OUT = hydraulic_check(networks, min_slope, min_vel_mps)
";

        // ================================================================== //
        //  Batch parameter updates
        // ================================================================== //

        /// <summary>
        /// Returns a Python script that reads a CSV file (handle, param name,
        /// value) and applies the values to Civil 3D objects via their
        /// PropertiesForTranslation extended data or XRecord.
        /// </summary>
        public static string BuildBatchParameterUpdateScript() => Preamble() + @"
import csv
import os
from Autodesk.AutoCAD.DatabaseServices import (
    OpenMode, DBObject, ResultBuffer, TypedValue
)
from Autodesk.AutoCAD.Runtime import ErrorStatus

# ---- Inputs ----
csv_path = IN[0]   # path to CSV file

# CSV format (no header row required; first row treated as header):
#   object_handle, parameter_name, new_value
# Example:
#   4A2,Description,Storm Drain Main
#   4A3,ReferenceDepth,1.500

doc = Application.DocumentManager.MdiActiveDocument
db  = doc.Database

def batch_update_parameters(csv_file_path):
    results  = {'updated': [], 'failed': [], 'errors': []}
    if not os.path.isfile(str(csv_file_path)):
        results['errors'].append(f'File not found: {csv_file_path}')
        return results

    try:
        with open(str(csv_file_path), newline='', encoding='utf-8-sig') as f:
            reader   = csv.DictReader(f)
            rows     = list(reader)
    except Exception as re:
        results['errors'].append(f'CSV read error: {re}')
        return results

    try:
        with doc.LockDocument():
            with db.TransactionManager.StartTransaction() as tr:
                for row in rows:
                    handle_str = (row.get('object_handle') or row.get('handle') or '').strip()
                    param_name = (row.get('parameter_name') or row.get('param') or '').strip()
                    new_value  = (row.get('new_value') or row.get('value') or '').strip()
                    if not handle_str or not param_name:
                        results['failed'].append({'row': row, 'reason': 'Missing handle or parameter_name'})
                        continue
                    try:
                        handle  = Handle(handle_str)
                        obj_id  = db.GetObjectId(False, handle, 0)
                        obj     = tr.GetObject(obj_id, OpenMode.ForWrite)
                        # Attempt to set via property reflection first
                        prop = type(obj).GetProperty(param_name)
                        if prop is not None and prop.CanWrite:
                            # Coerce value type
                            existing_val = prop.GetValue(obj)
                            if isinstance(existing_val, float):
                                prop.SetValue(obj, float(new_value))
                            elif isinstance(existing_val, int):
                                prop.SetValue(obj, int(new_value))
                            else:
                                prop.SetValue(obj, new_value)
                        else:
                            # Fallback: store in XRecord under the Civil3DConnector dict
                            app_name  = 'Civil3DConnector'
                            xdata_key = f'{param_name}'
                            reg_table = db.RegAppTable
                            if not reg_table.Has(app_name):
                                with db.TransactionManager.StartTransaction() as inner_tr:
                                    from Autodesk.AutoCAD.DatabaseServices import RegAppTableRecord
                                    reg = RegAppTableRecord()
                                    reg.Name = app_name
                                    reg_tbl  = inner_tr.GetObject(db.RegAppTableId, OpenMode.ForWrite)
                                    reg_tbl.Add(reg)
                                    inner_tr.AddNewlyCreatedDBObject(reg, True)
                                    inner_tr.Commit()
                            rb = ResultBuffer(
                                TypedValue(1001, app_name),
                                TypedValue(1000, xdata_key),
                                TypedValue(1000, new_value)
                            )
                            obj.XData = rb
                        results['updated'].append({
                            'handle': handle_str,
                            'param':  param_name,
                            'value':  new_value,
                        })
                    except Exception as oe:
                        results['failed'].append({
                            'handle': handle_str,
                            'param':  param_name,
                            'reason': str(oe),
                        })
                tr.Commit()
    except Exception as ex:
        results['errors'].append(str(ex))
    return results

OUT = batch_update_parameters(csv_path)
";

        // ================================================================== //
        //  Helper / utility scripts
        // ================================================================== //

        /// <summary>
        /// Returns a Python script that lists all Civil 3D objects in the
        /// drawing by type and outputs a summary table - useful for debugging.
        /// </summary>
        public static string BuildDocumentInventoryScript() => Preamble() + @"
from Autodesk.Civil.ApplicationServices import CivilApplication
from Autodesk.Civil.DatabaseServices import (
    Alignment, TinSurface, Corridor, Network,
    Profile, Assembly, SampleLineGroup
)
from Autodesk.AutoCAD.DatabaseServices import (
    OpenMode, BlockTableRecord
)

doc    = Application.DocumentManager.MdiActiveDocument
db     = doc.Database
c3d_db = CivilApplication.ActiveDocument

def inventory_document():
    inv    = {}
    errors = []
    categories = {
        'Alignments':    c3d_db.GetAlignmentIds,
        'TinSurfaces':   c3d_db.GetSurfaceIds,
        'Corridors':     c3d_db.GetCorridorIds,
        'PipeNetworks':  c3d_db.GetNetworkIds,
        'Assemblies':    c3d_db.GetAssemblyIds,
    }
    try:
        with db.TransactionManager.StartTransaction() as tr:
            for category, id_getter in categories.items():
                names = []
                try:
                    for obj_id in id_getter():
                        try:
                            obj = tr.GetObject(obj_id, OpenMode.ForRead)
                            names.append(str(obj.Name))
                        except Exception as ie:
                            names.append(f'<error: {ie}>')
                except Exception as ge:
                    errors.append(f'{category}: {ge}')
                inv[category] = names
    except Exception as ex:
        errors.append(str(ex))
    inv['errors'] = errors
    return inv

OUT = inventory_document()
";

        // ================================================================== //
        //  Private helpers
        // ================================================================== //

        /// <summary>
        /// Returns the standard Python preamble (clr references and imports)
        /// that every generated script begins with.
        /// </summary>
        private static string Preamble() => @"import clr
import sys

# ---- Compatibility guard (IronPython 2 vs CPython 3) ----
_is_cpython = sys.implementation.name == 'cpython' if hasattr(sys, 'implementation') else False

clr.AddReference('AutoCAD.ApplicationServices.Core')
clr.AddReference('AutoCAD.DatabaseServices.Interop')
clr.AddReference('AutoCAD.Geometry')
clr.AddReference('AeccDbMgd')
clr.AddReference('AecBaseMgd')

from Autodesk.AutoCAD.ApplicationServices import Application
from Autodesk.AutoCAD.DatabaseServices import (
    Transaction, OpenMode, Handle
)
from Autodesk.AutoCAD.Geometry import Point3d, Vector3d
from Autodesk.Civil.ApplicationServices import CivilApplication
from Autodesk.Civil.DatabaseServices import (
    Alignment, TinSurface, Profile, Corridor, Network
)

";

        // ------------------------------------------------------------------ //
        //  Script catalogue
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Enumeration of all named scripts that this class can generate.
        /// </summary>
        public enum ScriptType
        {
            SurfaceSampler,
            CutFillVolume,
            ContourExtraction,
            SurfaceBreakline,
            AlignmentGeometry,
            ProfileData,
            ProfilePviEdit,
            CorridorSectionExtractor,
            PipeInventory,
            PipeHydraulicCheck,
            BatchParameterUpdate,
            DocumentInventory,
        }

        /// <summary>
        /// Returns the generated Python script string for the given
        /// <see cref="ScriptType"/>.
        /// </summary>
        public static string Build(ScriptType scriptType) => scriptType switch
        {
            ScriptType.SurfaceSampler          => BuildSurfaceSamplerScript(),
            ScriptType.CutFillVolume           => BuildCutFillVolumeScript(),
            ScriptType.ContourExtraction       => BuildContourExtractionScript(),
            ScriptType.SurfaceBreakline        => BuildSurfaceBreaklineScript(),
            ScriptType.AlignmentGeometry       => BuildAlignmentGeometryScript(),
            ScriptType.ProfileData             => BuildProfileDataScript(),
            ScriptType.ProfilePviEdit          => BuildProfilePviEditScript(),
            ScriptType.CorridorSectionExtractor => BuildCorridorSectionExtractorScript(),
            ScriptType.PipeInventory           => BuildPipeInventoryScript(),
            ScriptType.PipeHydraulicCheck      => BuildPipeHydraulicCheckScript(),
            ScriptType.BatchParameterUpdate    => BuildBatchParameterUpdateScript(),
            ScriptType.DocumentInventory       => BuildDocumentInventoryScript(),
            _ => throw new ArgumentOutOfRangeException(nameof(scriptType), scriptType, null)
        };

        /// <summary>
        /// Returns the names of all available script types.
        /// </summary>
        public static IReadOnlyList<string> AllScriptNames
            => Enum.GetNames(typeof(ScriptType));
    }
}
