"""
surface_operations.py
=====================
Dynamo CPython script for Civil 3D surface operations.

Operations
----------
- extract_contours         : Extract contour polylines from a TIN surface
- calculate_cut_fill       : Compute cut/fill volumes between two surfaces
- sample_elevation_at_points: Query elevations at a list of XY points
- get_slope_at_point       : Compute slope and aspect at a point
- create_surface_from_points: Build a new TIN surface from Point3d list
- export_surface_to_csv    : Write surface grid to CSV
- export_surface_to_landxml: Export surface to LandXML 1.2

Usage in a Dynamo Python node
------------------------------
    import sys
    sys.path.insert(0, r"<folder containing this file>")
    from surface_operations import extract_contours, sample_elevation_at_points

    surface_name = IN[0]   # string
    ...
    OUT = extract_contours(session.active_document, surface_name)
"""

from __future__ import annotations

import csv
import math
import os
import traceback
from typing import Any, Dict, List, Optional, Tuple
from xml.etree import ElementTree as ET

# ---------------------------------------------------------------------------
# Bootstrap Civil 3D CLR references (same approach as civil3d_helpers.py)
# ---------------------------------------------------------------------------

try:
    import clr  # type: ignore

    _REQUIRED = [
        "AcMgd", "AcDbMgd", "AcCoreMgd",
        "Autodesk.Civil.ApplicationServices",
        "Autodesk.Civil.DatabaseServices",
        "AeccXUiLand",
    ]
    for _asm in _REQUIRED:
        try:
            clr.AddReference(_asm)
        except Exception:
            pass
    _CLR_OK = True
except ImportError:
    _CLR_OK = False


# ---------------------------------------------------------------------------
# Helper types
# ---------------------------------------------------------------------------

ContourData   = List[Dict[str, Any]]  # list of {"elevation": float, "points": [...]}
ElevationData = List[Dict[str, Any]]  # list of {"x", "y", "elevation"}
VolumeResult  = Dict[str, float]      # {"cut_m3", "fill_m3", "net_m3"}
SlopeResult   = Dict[str, float]      # {"slope_pct", "slope_deg", "aspect_deg"}


# ---------------------------------------------------------------------------
# 1. Extract contours
# ---------------------------------------------------------------------------

def extract_contours(
    civil_doc,
    surface_name: str,
    minor_interval: float = 1.0,
    major_interval: float = 5.0,
) -> ContourData:
    """
    Extract contour line geometry from a named TIN surface.

    Parameters
    ----------
    civil_doc      : CivilDocument (from Civil3DSession.active_document)
    surface_name   : str  – name of the surface in the drawing
    minor_interval : float – contour interval for minor contours [m]
    major_interval : float – contour interval for major contours [m]

    Returns
    -------
    list of dict
        Each dict has keys:
        - "elevation"  : float
        - "type"       : "minor" | "major"
        - "points"     : list of {"x": float, "y": float, "z": float}
    """
    _assert_clr()
    try:
        import Autodesk.AutoCAD.DatabaseServices as AcDb  # type: ignore
        from Autodesk.Civil.DatabaseServices import TinSurface  # type: ignore

        results: ContourData = []

        with AcDb.Transaction() as tr:
            surf = _get_tin_surface(civil_doc, surface_name, tr)
            if surf is None:
                raise KeyError(f"TIN surface '{surface_name}' not found.")

            stats = surf.GetGeneralProperties()
            z_min = float(stats.MinimumElevation)
            z_max = float(stats.MaximumElevation)

            # Minor contours
            z = _round_up_to_interval(z_min, minor_interval)
            while z <= z_max:
                elev_type = "major" if abs(z % major_interval) < 1e-6 else "minor"
                contour_pts = surf.GetContour(z)
                if contour_pts is not None:
                    for loop in contour_pts:
                        pts = [
                            {"x": float(p.X), "y": float(p.Y), "z": float(p.Z)}
                            for p in loop
                        ]
                        if pts:
                            results.append(
                                {"elevation": z, "type": elev_type, "points": pts}
                            )
                z = round(z + minor_interval, 6)

            tr.Commit()

        return results

    except Exception as exc:
        raise RuntimeError(
            f"extract_contours failed for '{surface_name}': {exc}\n"
            f"{traceback.format_exc()}"
        ) from exc


# ---------------------------------------------------------------------------
# 2. Calculate cut/fill volumes
# ---------------------------------------------------------------------------

def calculate_cut_fill(
    civil_doc,
    base_surface_name: str,
    comparison_surface_name: str,
) -> VolumeResult:
    """
    Calculate cut and fill volumes between two surfaces using a TIN
    volume surface.

    Parameters
    ----------
    civil_doc                : CivilDocument
    base_surface_name        : str – typically the existing ground surface
    comparison_surface_name  : str – typically the finished grade surface

    Returns
    -------
    dict with keys:
        "cut_m3"  : float – total cut volume (positive)
        "fill_m3" : float – total fill volume (positive)
        "net_m3"  : float – net (cut minus fill, positive = net cut)
    """
    _assert_clr()
    try:
        import Autodesk.AutoCAD.DatabaseServices as AcDb              # type: ignore
        from Autodesk.Civil.DatabaseServices import TinVolumeSurface  # type: ignore

        vol_surface_name = f"_Vol_{base_surface_name}_{comparison_surface_name}"

        with AcDb.Transaction() as tr:
            base_surf = _get_tin_surface(civil_doc, base_surface_name, tr)
            comp_surf = _get_tin_surface(civil_doc, comparison_surface_name, tr)

            if base_surf is None:
                raise KeyError(f"Base surface '{base_surface_name}' not found.")
            if comp_surf is None:
                raise KeyError(
                    f"Comparison surface '{comparison_surface_name}' not found."
                )

            db   = civil_doc.Database
            surfs = civil_doc.GetSurfaceIds()

            # Re-use an existing volume surface if present.
            vol_surf = None
            for sid in surfs:
                s = tr.GetObject(sid, AcDb.OpenMode.ForRead)
                if (
                    isinstance(s, TinVolumeSurface)
                    and s.Name == vol_surface_name
                ):
                    vol_surf = s
                    break

            if vol_surf is None:
                vol_surf = TinVolumeSurface.Create(
                    db,
                    base_surf.ObjectId,
                    comp_surf.ObjectId,
                    vol_surface_name,
                )

            vol_stats = vol_surf.GetVolumeProperties()
            cut_m3  = float(vol_stats.CutVolume)
            fill_m3 = float(vol_stats.FillVolume)

            tr.Commit()

        return {
            "cut_m3":  cut_m3,
            "fill_m3": fill_m3,
            "net_m3":  cut_m3 - fill_m3,
        }

    except Exception as exc:
        raise RuntimeError(
            f"calculate_cut_fill failed: {exc}\n{traceback.format_exc()}"
        ) from exc


# ---------------------------------------------------------------------------
# 3. Sample elevation at points
# ---------------------------------------------------------------------------

def sample_elevation_at_points(
    civil_doc,
    surface_name: str,
    xy_points: List[Tuple[float, float]],
) -> ElevationData:
    """
    Query surface elevations at a list of (x, y) coordinate pairs.

    Parameters
    ----------
    civil_doc    : CivilDocument
    surface_name : str
    xy_points    : list of (x, y) tuples in the drawing coordinate system

    Returns
    -------
    list of dict {"x", "y", "elevation", "valid"}
        "valid" is False if the point lies outside the surface boundary.
    """
    _assert_clr()
    try:
        import Autodesk.AutoCAD.DatabaseServices as AcDb  # type: ignore
        import Autodesk.AutoCAD.Geometry as AcGe           # type: ignore

        results: ElevationData = []

        with AcDb.Transaction() as tr:
            surf = _get_tin_surface(civil_doc, surface_name, tr)
            if surf is None:
                raise KeyError(f"Surface '{surface_name}' not found.")

            for x, y in xy_points:
                try:
                    pt = AcGe.Point2d(x, y)
                    z  = surf.FindElevationAtXY(x, y)
                    results.append(
                        {"x": x, "y": y, "elevation": float(z), "valid": True}
                    )
                except Exception:
                    results.append(
                        {"x": x, "y": y, "elevation": None, "valid": False}
                    )

            tr.Commit()

        return results

    except Exception as exc:
        raise RuntimeError(
            f"sample_elevation_at_points failed: {exc}\n{traceback.format_exc()}"
        ) from exc


# ---------------------------------------------------------------------------
# 4. Get slope at point
# ---------------------------------------------------------------------------

def get_slope_at_point(
    civil_doc,
    surface_name: str,
    x: float,
    y: float,
) -> SlopeResult:
    """
    Return slope and aspect at a single (x, y) location on a TIN surface.

    Parameters
    ----------
    civil_doc    : CivilDocument
    surface_name : str
    x, y         : float – plan coordinates

    Returns
    -------
    dict with keys:
        "slope_pct"  : float  (e.g. 5.0 for 5%)
        "slope_deg"  : float  (angle from horizontal in degrees)
        "aspect_deg" : float  (bearing of steepest descent, 0–360 clockwise from north)
    """
    _assert_clr()
    try:
        import Autodesk.AutoCAD.DatabaseServices as AcDb  # type: ignore

        with AcDb.Transaction() as tr:
            surf = _get_tin_surface(civil_doc, surface_name, tr)
            if surf is None:
                raise KeyError(f"Surface '{surface_name}' not found.")

            # Civil 3D API returns slope as a ratio (run/rise → dy/dx, dy/dy).
            slope_x = float(surf.FindSlopeAtXY(x, y).X)
            slope_y = float(surf.FindSlopeAtXY(x, y).Y)

            tr.Commit()

        # Magnitude of the gradient vector  (both components in rise/run form)
        gradient = math.sqrt(slope_x ** 2 + slope_y ** 2)
        slope_pct = gradient * 100.0
        slope_deg = math.degrees(math.atan(gradient))

        # Aspect: bearing of steepest descent (downhill direction)
        # slope_x, slope_y point in the uphill direction.
        aspect_rad = math.atan2(-slope_x, -slope_y)  # negate for downhill
        aspect_deg = (math.degrees(aspect_rad) + 360.0) % 360.0

        return {
            "slope_pct":  round(slope_pct, 4),
            "slope_deg":  round(slope_deg, 4),
            "aspect_deg": round(aspect_deg, 2),
        }

    except Exception as exc:
        raise RuntimeError(
            f"get_slope_at_point failed at ({x}, {y}): {exc}\n"
            f"{traceback.format_exc()}"
        ) from exc


# ---------------------------------------------------------------------------
# 5. Create surface from points
# ---------------------------------------------------------------------------

def create_surface_from_points(
    civil_doc,
    surface_name: str,
    points: List[Tuple[float, float, float]],
    style_name: str = "Standard",
) -> Any:
    """
    Create a new TIN surface and add a list of (x, y, z) points as input data.

    Parameters
    ----------
    civil_doc    : CivilDocument
    surface_name : str – name for the new surface (must be unique)
    points       : list of (x, y, z) tuples
    style_name   : str – Civil 3D surface style name

    Returns
    -------
    The newly created TinSurface object.
    """
    _assert_clr()
    try:
        import Autodesk.AutoCAD.DatabaseServices as AcDb         # type: ignore
        import Autodesk.AutoCAD.Geometry as AcGe                  # type: ignore
        from Autodesk.Civil.DatabaseServices import TinSurface    # type: ignore

        db = civil_doc.Database

        with AcDb.Transaction() as tr:
            # Resolve style ObjectId (falls back to first available style).
            style_id = _get_surface_style_id(civil_doc, style_name, tr)

            surf_id = TinSurface.Create(surface_name, style_id)
            surf    = tr.GetObject(surf_id, AcDb.OpenMode.ForWrite)

            # Add point input
            point_input = surf.DataOperationsManager.PointFiles  # or manual add
            for x, y, z in points:
                surf.DataOperationsManager.Add(
                    AcGe.Point3d(x, y, z)
                )

            surf.Rebuild()
            tr.Commit()

        return surf

    except Exception as exc:
        raise RuntimeError(
            f"create_surface_from_points failed for '{surface_name}': {exc}\n"
            f"{traceback.format_exc()}"
        ) from exc


# ---------------------------------------------------------------------------
# 6. Export surface to CSV
# ---------------------------------------------------------------------------

def export_surface_to_csv(
    civil_doc,
    surface_name: str,
    output_path: str,
    grid_spacing: float = 10.0,
) -> str:
    """
    Export a regular elevation grid sampled from the surface to a CSV file.

    Parameters
    ----------
    civil_doc    : CivilDocument
    surface_name : str
    output_path  : str – full path for the output .csv file
    grid_spacing : float – sample grid spacing in metres

    Returns
    -------
    str – absolute path of the written file
    """
    _assert_clr()
    try:
        import Autodesk.AutoCAD.DatabaseServices as AcDb  # type: ignore

        with AcDb.Transaction() as tr:
            surf = _get_tin_surface(civil_doc, surface_name, tr)
            if surf is None:
                raise KeyError(f"Surface '{surface_name}' not found.")

            props   = surf.GetGeneralProperties()
            x_min   = float(props.MinEasting)
            x_max   = float(props.MaxEasting)
            y_min   = float(props.MinNorthing)
            y_max   = float(props.MaxNorthing)
            z_min   = float(props.MinimumElevation)
            z_max   = float(props.MaximumElevation)

            rows: List[Dict[str, Any]] = []
            x = x_min
            while x <= x_max + 1e-9:
                y = y_min
                while y <= y_max + 1e-9:
                    try:
                        z = float(surf.FindElevationAtXY(x, y))
                        rows.append({"x": x, "y": y, "z": z})
                    except Exception:
                        pass  # point outside surface boundary
                    y += grid_spacing
                x += grid_spacing

            tr.Commit()

        os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
        with open(output_path, "w", newline="", encoding="utf-8") as fh:
            writer = csv.DictWriter(fh, fieldnames=["x", "y", "z"])
            writer.writeheader()
            writer.writerows(rows)

        return os.path.abspath(output_path)

    except Exception as exc:
        raise RuntimeError(
            f"export_surface_to_csv failed: {exc}\n{traceback.format_exc()}"
        ) from exc


# ---------------------------------------------------------------------------
# 7. Export surface to LandXML 1.2
# ---------------------------------------------------------------------------

def export_surface_to_landxml(
    civil_doc,
    surface_name: str,
    output_path: str,
    proj_name: str = "Civil3D Project",
) -> str:
    """
    Export TIN surface triangles and points to a LandXML 1.2 file.

    Parameters
    ----------
    civil_doc    : CivilDocument
    surface_name : str
    output_path  : str – full path for the output .xml file
    proj_name    : str – project name embedded in the LandXML header

    Returns
    -------
    str – absolute path of the written file
    """
    _assert_clr()
    try:
        import Autodesk.AutoCAD.DatabaseServices as AcDb  # type: ignore

        with AcDb.Transaction() as tr:
            surf = _get_tin_surface(civil_doc, surface_name, tr)
            if surf is None:
                raise KeyError(f"Surface '{surface_name}' not found.")

            # Collect triangles
            triangles = []
            for tri in surf.GetTriangles(False):  # False = non-deleted only
                p1 = tri.Vertex1.Location
                p2 = tri.Vertex2.Location
                p3 = tri.Vertex3.Location
                triangles.append((
                    (float(p1.X), float(p1.Y), float(p1.Z)),
                    (float(p2.X), float(p2.Y), float(p2.Z)),
                    (float(p3.X), float(p3.Y), float(p3.Z)),
                ))

            # Collect unique vertices
            vertex_set: Dict[Tuple[float, float, float], int] = {}
            for tri in triangles:
                for v in tri:
                    if v not in vertex_set:
                        vertex_set[v] = len(vertex_set) + 1  # 1-based index

            tr.Commit()

        # Build LandXML document
        root = ET.Element("LandXML", {
            "xmlns":         "http://www.landxml.org/schema/LandXML-1.2",
            "xmlns:xsi":     "http://www.w3.org/2001/XMLSchema-instance",
            "version":       "1.2",
            "date":          "2024-01-01",
            "time":          "00:00:00",
            "language":      "English",
        })

        project_el = ET.SubElement(root, "Project", {"name": proj_name})

        surfaces_el = ET.SubElement(root, "Surfaces")
        surf_el     = ET.SubElement(surfaces_el, "Surface", {
            "name": surface_name,
            "desc": f"Exported from Civil 3D – {surface_name}",
        })

        def_el  = ET.SubElement(surf_el, "Definition", {"surfType": "TIN"})
        pnts_el = ET.SubElement(def_el, "Pnts")
        for (x, y, z), idx in sorted(vertex_set.items(), key=lambda kv: kv[1]):
            ET.SubElement(pnts_el, "P", {"id": str(idx)}).text = (
                f"{y:.4f} {x:.4f} {z:.4f}"   # LandXML: northing easting elevation
            )

        faces_el = ET.SubElement(def_el, "Faces")
        for tri in triangles:
            i1 = vertex_set[tri[0]]
            i2 = vertex_set[tri[1]]
            i3 = vertex_set[tri[2]]
            ET.SubElement(faces_el, "F").text = f"{i1} {i2} {i3}"

        tree = ET.ElementTree(root)
        ET.indent(tree, space="  ")

        os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
        tree.write(
            output_path,
            encoding="utf-8",
            xml_declaration=True,
        )

        return os.path.abspath(output_path)

    except Exception as exc:
        raise RuntimeError(
            f"export_surface_to_landxml failed: {exc}\n{traceback.format_exc()}"
        ) from exc


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _assert_clr() -> None:
    if not _CLR_OK:
        raise RuntimeError(
            "CLR / Civil 3D assemblies are not available. "
            "This script must run inside the Dynamo CPython engine."
        )


def _get_tin_surface(civil_doc, name: str, tr):
    """
    Return a TinSurface opened for read within *tr*, or None if not found.
    """
    try:
        from Autodesk.Civil.DatabaseServices import TinSurface  # type: ignore
        import Autodesk.AutoCAD.DatabaseServices as AcDb        # type: ignore

        name_lower = name.strip().lower()
        for sid in civil_doc.GetSurfaceIds():
            obj = tr.GetObject(sid, AcDb.OpenMode.ForRead)
            if isinstance(obj, TinSurface) and obj.Name.lower() == name_lower:
                return obj
        return None
    except Exception:
        return None


def _get_surface_style_id(civil_doc, style_name: str, tr):
    """Resolve a surface style ObjectId by name, falling back to the first available."""
    try:
        import Autodesk.AutoCAD.DatabaseServices as AcDb  # type: ignore

        styles = civil_doc.Styles.SurfaceStyles
        name_lower = style_name.lower()
        for sid in styles:
            s = tr.GetObject(sid, AcDb.OpenMode.ForRead)
            if s.Name.lower() == name_lower:
                return sid
        # Fallback: return first style
        for sid in styles:
            return sid
    except Exception:
        pass
    return None


def _round_up_to_interval(value: float, interval: float) -> float:
    """Round *value* up to the next multiple of *interval*."""
    if interval <= 0:
        return value
    return math.ceil(value / interval) * interval
