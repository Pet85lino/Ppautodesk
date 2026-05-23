"""
Alignment Geometry Extraction for Dynamo (CPython 3.x Engine)
Civil 3D 2025 / 2026 / 2027 Compatible

Extracts alignment geometry as native Python objects for analysis,
GIS export, or further processing in Dynamo workflows.
"""

import clr
import sys
import math
from typing import List, Tuple, Dict, Optional

# ─────────────────────────────────────────────────────────────────────────────
# AutoCAD / Civil 3D References
# ─────────────────────────────────────────────────────────────────────────────

clr.AddReference("accoremgd")
clr.AddReference("acdbmgd")
clr.AddReference("AcMgd")
clr.AddReference("Autodesk.Civil.ApplicationServices")
clr.AddReference("Autodesk.Civil.DatabaseServices")

from Autodesk.AutoCAD.ApplicationServices import Application
from Autodesk.AutoCAD.DatabaseServices import (
    Transaction, OpenMode, ObjectId
)
from Autodesk.Civil.ApplicationServices import CivilApplication
from Autodesk.Civil.DatabaseServices import (
    Alignment, AlignmentEntityType, AlignmentLine,
    AlignmentArc, AlignmentSpiral
)

# ─────────────────────────────────────────────────────────────────────────────
# Main Functions
# ─────────────────────────────────────────────────────────────────────────────

def get_all_alignments() -> List[Dict]:
    """
    Returns a list of dicts with alignment info for all alignments in the document.
    Each dict: { name, id, length, startStation, endStation, layer }
    """
    doc = Application.DocumentManager.MdiActiveDocument
    civil_doc = CivilApplication.ActiveDocument
    db = doc.Database

    alignments_data = []
    with doc.LockDocument():
        with db.TransactionManager.StartOpenCloseTransaction() as tr:
            for align_id in civil_doc.GetAlignmentIds():
                align = tr.GetObject(align_id, OpenMode.ForRead)
                if align is None:
                    continue
                alignments_data.append({
                    "name": align.Name,
                    "id": str(align_id),
                    "length": round(align.Length, 4),
                    "startStation": round(align.StartingStation, 4),
                    "endStation": round(align.EndingStation, 4),
                    "layer": align.Layer,
                    "description": align.Description
                })
    return alignments_data


def get_alignment_by_name(name: str) -> Optional[Dict]:
    """Gets alignment data by name. Returns None if not found."""
    alignments = get_all_alignments()
    return next((a for a in alignments if a["name"] == name), None)


def sample_alignment_points(alignment_name: str, interval_m: float = 5.0) -> List[Tuple[float, float, float]]:
    """
    Samples XY points along an alignment at the given station interval.
    Returns list of (X, Y, station) tuples.

    Args:
        alignment_name: Name of the Civil 3D alignment
        interval_m: Sampling interval in meters (default 5m)

    Returns:
        List of (X, Y, station) tuples
    """
    doc = Application.DocumentManager.MdiActiveDocument
    civil_doc = CivilApplication.ActiveDocument
    db = doc.Database
    points = []

    with doc.LockDocument():
        with db.TransactionManager.StartOpenCloseTransaction() as tr:
            target_align = None
            for align_id in civil_doc.GetAlignmentIds():
                align = tr.GetObject(align_id, OpenMode.ForRead)
                if align.Name == alignment_name:
                    target_align = align
                    break

            if target_align is None:
                raise ValueError(f"Alignment '{alignment_name}' not found in the drawing.")

            station = target_align.StartingStation
            end_station = target_align.EndingStation

            while station <= end_station:
                x, y = 0.0, 0.0
                target_align.PointLocation(station, 0, x, y)
                points.append((round(x, 4), round(y, 4), round(station, 4)))
                station += interval_m

            # Always include the end point
            target_align.PointLocation(end_station, 0, x, y)
            points.append((round(x, 4), round(y, 4), round(end_station, 4)))

    return points


def extract_alignment_entities(alignment_name: str) -> List[Dict]:
    """
    Extracts individual geometric entities (lines, arcs, spirals) from an alignment.

    Returns list of entity dicts with type-specific geometry data.
    """
    doc = Application.DocumentManager.MdiActiveDocument
    civil_doc = CivilApplication.ActiveDocument
    db = doc.Database
    entities = []

    with doc.LockDocument():
        with db.TransactionManager.StartOpenCloseTransaction() as tr:
            target = None
            for align_id in civil_doc.GetAlignmentIds():
                a = tr.GetObject(align_id, OpenMode.ForRead)
                if a.Name == alignment_name:
                    target = a
                    break

            if target is None:
                raise ValueError(f"Alignment '{alignment_name}' not found.")

            for entity in target.Entities:
                entity_type = str(entity.EntityType)
                base = {
                    "type": entity_type,
                    "startStation": round(entity.StartStation, 4),
                    "endStation": round(entity.EndStation, 4),
                    "length": round(entity.Length, 4)
                }

                if entity.EntityType == AlignmentEntityType.Line:
                    line = entity  # AlignmentLine
                    base.update({
                        "direction_deg": round(math.degrees(line.Direction), 4),
                        "startX": round(line.StartPoint.X, 4),
                        "startY": round(line.StartPoint.Y, 4),
                        "endX": round(line.EndPoint.X, 4),
                        "endY": round(line.EndPoint.Y, 4)
                    })

                elif entity.EntityType == AlignmentEntityType.Arc:
                    arc = entity  # AlignmentArc
                    base.update({
                        "radius": round(arc.Radius, 4),
                        "delta_deg": round(math.degrees(arc.Delta), 4),
                        "direction": "CW" if arc.Clockwise else "CCW",
                        "centerX": round(arc.CenterPoint.X, 4),
                        "centerY": round(arc.CenterPoint.Y, 4)
                    })

                elif entity.EntityType == AlignmentEntityType.SpiralCurveSpiral:
                    spiral = entity
                    base.update({
                        "spiralType": str(spiral.SpiralIn.SpiralType),
                        "spiralInLength": round(spiral.SpiralIn.Length, 4),
                        "arcLength": round(spiral.CurveEntity.Length, 4),
                        "spiralOutLength": round(spiral.SpiralOut.Length, 4)
                    })

                entities.append(base)

    return entities


def get_station_offset_from_point(alignment_name: str, x: float, y: float) -> Tuple[float, float]:
    """
    Finds the station and offset for a given XY point relative to the alignment.

    Returns:
        (station, offset) where offset is positive to the right of the alignment direction
    """
    doc = Application.DocumentManager.MdiActiveDocument
    civil_doc = CivilApplication.ActiveDocument
    db = doc.Database

    with doc.LockDocument():
        with db.TransactionManager.StartOpenCloseTransaction() as tr:
            for align_id in civil_doc.GetAlignmentIds():
                a = tr.GetObject(align_id, OpenMode.ForRead)
                if a.Name == alignment_name:
                    station, offset = 0.0, 0.0
                    a.StationOffset(x, y, station, offset)
                    return (round(station, 4), round(offset, 4))

    raise ValueError(f"Alignment '{alignment_name}' not found.")


def calculate_bearing_distance(
    station1: float, station2: float,
    alignment_name: str
) -> Dict:
    """
    Calculates bearing and distance between two stations on an alignment.

    Returns:
        Dict with 'bearing_deg', 'distance_m'
    """
    doc = Application.DocumentManager.MdiActiveDocument
    civil_doc = CivilApplication.ActiveDocument
    db = doc.Database

    with doc.LockDocument():
        with db.TransactionManager.StartOpenCloseTransaction() as tr:
            for align_id in civil_doc.GetAlignmentIds():
                a = tr.GetObject(align_id, OpenMode.ForRead)
                if a.Name == alignment_name:
                    x1, y1 = 0.0, 0.0
                    x2, y2 = 0.0, 0.0
                    a.PointLocation(station1, 0, x1, y1)
                    a.PointLocation(station2, 0, x2, y2)

                    dx = x2 - x1
                    dy = y2 - y1
                    dist = math.sqrt(dx * dx + dy * dy)
                    bearing = math.degrees(math.atan2(dx, dy)) % 360

                    return {
                        "bearing_deg": round(bearing, 4),
                        "distance_m": round(dist, 4),
                        "deltaX": round(dx, 4),
                        "deltaY": round(dy, 4)
                    }

    raise ValueError(f"Alignment '{alignment_name}' not found.")


def export_alignment_to_dict(alignment_name: str) -> Dict:
    """
    Full export of alignment data including geometry, PIs, and entities.
    Suitable for JSON serialization and GIS workflows.
    """
    basic = get_alignment_by_name(alignment_name)
    if basic is None:
        raise ValueError(f"Alignment '{alignment_name}' not found.")

    entities = extract_alignment_entities(alignment_name)
    sampled_points = sample_alignment_points(alignment_name, interval_m=20.0)

    return {
        **basic,
        "entities": entities,
        "entityCount": len(entities),
        "sampledPoints": [{"x": p[0], "y": p[1], "station": p[2]} for p in sampled_points],
        "sampledPointCount": len(sampled_points)
    }


# ─────────────────────────────────────────────────────────────────────────────
# Dynamo entry point (when script is used as a Dynamo Python node)
# ─────────────────────────────────────────────────────────────────────────────
#
# Inputs (from Dynamo IN list):
#   IN[0]: str  - Alignment name
#   IN[1]: float - Sampling interval in meters (default 5.0)
#
# Outputs (Dynamo OUT):
#   List of [X, Y, station] lists
#

if "IN" in dir():
    _alignment_name = IN[0] if len(IN) > 0 else ""
    _interval = float(IN[1]) if len(IN) > 1 else 5.0

    if _alignment_name:
        OUT = sample_alignment_points(_alignment_name, _interval)
    else:
        OUT = get_all_alignments()
