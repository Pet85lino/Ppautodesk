"""
Corridor Earthwork Quantities for Dynamo (CPython 3.x)
Civil 3D 2025 / 2026 / 2027

Extracts cross-section areas and calculates earthwork volumes.
Follows the MTOP Ecuador prismatoid method.
"""

import clr
import sys
from typing import List, Dict, Tuple, Optional
from collections import defaultdict

clr.AddReference("accoremgd")
clr.AddReference("acdbmgd")
clr.AddReference("Autodesk.Civil.ApplicationServices")
clr.AddReference("Autodesk.Civil.DatabaseServices")

from Autodesk.AutoCAD.ApplicationServices import Application
from Autodesk.AutoCAD.DatabaseServices import OpenMode
from Autodesk.Civil.ApplicationServices import CivilApplication
from Autodesk.Civil.DatabaseServices import Corridor


def get_all_corridors() -> List[Dict]:
    """Returns basic info for all corridors in the document."""
    doc = Application.DocumentManager.MdiActiveDocument
    civil_doc = CivilApplication.ActiveDocument
    db = doc.Database
    corridors = []

    with doc.LockDocument():
        with db.TransactionManager.StartOpenCloseTransaction() as tr:
            for corr_id in civil_doc.GetCorridorIds():
                corr = tr.GetObject(corr_id, OpenMode.ForRead)
                corridors.append({
                    "name": corr.Name,
                    "id": str(corr_id),
                    "upToDate": corr.BuildStatusIsUpToDate,
                    "baselineCount": corr.Baselines.Count
                })
    return corridors


def extract_cross_section_data(corridor_name: str, baseline_name: Optional[str] = None) -> List[Dict]:
    """
    Extracts cross-section point data from a corridor.

    Returns list of sections, each with:
      station, baseline, list of {offset, elevation, code}
    """
    doc = Application.DocumentManager.MdiActiveDocument
    civil_doc = CivilApplication.ActiveDocument
    db = doc.Database
    sections_data = []

    with doc.LockDocument():
        with db.TransactionManager.StartOpenCloseTransaction() as tr:
            corr = None
            for corr_id in civil_doc.GetCorridorIds():
                c = tr.GetObject(corr_id, OpenMode.ForRead)
                if c.Name == corridor_name:
                    corr = c
                    break

            if corr is None:
                raise ValueError(f"Corridor '{corridor_name}' not found.")

            for baseline in corr.Baselines:
                if baseline_name and baseline.Name != baseline_name:
                    continue

                for region in baseline.BaselineRegions:
                    for section in region.GetCorridorSections():
                        pts = []
                        for pt in section.GetCorridorPoints():
                            pts.append({
                                "offset": round(pt.OffsetFromBaseline, 4),
                                "elevation": round(pt.Elevation, 4),
                                "code": pt.PointCode
                            })

                        sections_data.append({
                            "baseline": baseline.Name,
                            "station": round(section.Station, 4),
                            "points": pts
                        })

    return sections_data


def calculate_section_areas(section_points: List[Dict]) -> Tuple[float, float]:
    """
    Calculates cut and fill areas for a cross-section using the shoelace formula.

    Args:
        section_points: List of {offset, elevation, code} from extract_cross_section_data

    Returns:
        (cut_area_m2, fill_area_m2)
    """
    if not section_points:
        return 0.0, 0.0

    cut_pts = [(p["offset"], p["elevation"]) for p in section_points if "Cut" in p.get("code", "")]
    fill_pts = [(p["offset"], p["elevation"]) for p in section_points if "Fill" in p.get("code", "")]

    def shoelace(pts: List[Tuple[float, float]]) -> float:
        if len(pts) < 3:
            return 0.0
        n = len(pts)
        area = 0.0
        for i in range(n):
            j = (i + 1) % n
            area += pts[i][0] * pts[j][1]
            area -= pts[j][0] * pts[i][1]
        return abs(area) / 2.0

    return shoelace(cut_pts), shoelace(fill_pts)


def calculate_earthwork_volumes(corridor_name: str) -> List[Dict]:
    """
    Calculates earthwork cut/fill volumes between consecutive stations.
    Uses the Prismatoid (average end area) method per MTOP Ecuador.

    Returns:
        List of station intervals with volumes:
        { startStation, endStation, avgCutArea, avgFillArea, cutVolume, fillVolume, netVolume }
    """
    sections = extract_cross_section_data(corridor_name)
    if len(sections) < 2:
        return []

    volumes = []
    for i in range(len(sections) - 1):
        s1 = sections[i]
        s2 = sections[i + 1]

        cut1, fill1 = calculate_section_areas(s1["points"])
        cut2, fill2 = calculate_section_areas(s2["points"])
        dist = s2["station"] - s1["station"]

        if dist <= 0:
            continue

        # Average end area method (prismatoid): V = L/2 * (A1 + A2)
        cut_vol = (dist / 2.0) * (cut1 + cut2)
        fill_vol = (dist / 2.0) * (fill1 + fill2)

        volumes.append({
            "startStation": round(s1["station"], 2),
            "endStation": round(s2["station"], 2),
            "distance": round(dist, 4),
            "avgCutArea": round((cut1 + cut2) / 2, 4),
            "avgFillArea": round((fill1 + fill2) / 2, 4),
            "cutVolume": round(cut_vol, 4),
            "fillVolume": round(fill_vol, 4),
            "netVolume": round(fill_vol - cut_vol, 4)
        })

    return volumes


def generate_mass_haul_diagram(corridor_name: str) -> Dict:
    """
    Generates mass haul (diagrama de masas) data for a corridor.
    Cumulative cut-fill balance following MTOP methodology.

    Returns dict with stations, cumulative volumes, and balance.
    """
    volumes = calculate_earthwork_volumes(corridor_name)
    if not volumes:
        return {"error": f"No volume data for corridor '{corridor_name}'"}

    # Apply a shrinkage factor of 1.25 for fill (typical soil Ecuador)
    SHRINKAGE = 1.25

    cumulative = 0.0
    stations = [volumes[0]["startStation"]]
    cum_volumes = [0.0]

    for v in volumes:
        # Cut adds to balance (material available); fill subtracts
        cumulative += v["cutVolume"] - (v["fillVolume"] * SHRINKAGE)
        stations.append(v["endStation"])
        cum_volumes.append(round(cumulative, 2))

    total_cut = sum(v["cutVolume"] for v in volumes)
    total_fill = sum(v["fillVolume"] for v in volumes)
    net_balance = total_cut - (total_fill * SHRINKAGE)

    return {
        "corridorName": corridor_name,
        "shrinkageFactor": SHRINKAGE,
        "totalCutM3": round(total_cut, 2),
        "totalFillM3": round(total_fill, 2),
        "totalFillAdjustedM3": round(total_fill * SHRINKAGE, 2),
        "netBalanceM3": round(net_balance, 2),
        "balanceType": "Exceso de Corte" if net_balance > 0 else "Déficit (Préstamo requerido)",
        "stations": stations,
        "cumulativeVolumes": cum_volumes
    }


def export_to_excel(corridor_name: str, output_path: str):
    """
    Exports earthwork quantities to Excel (XLSX) using openpyxl.
    Creates a professional report with stations and volumes.
    """
    try:
        import openpyxl
        from openpyxl.styles import Font, PatternFill, Alignment, Border, Side
        from openpyxl.utils import get_column_letter
    except ImportError:
        raise ImportError("openpyxl is required. Install via: pip install openpyxl")

    volumes = calculate_earthwork_volumes(corridor_name)
    mass_haul = generate_mass_haul_diagram(corridor_name)

    wb = openpyxl.Workbook()
    ws = wb.active
    ws.title = "Cómputos Métricos"

    # Title
    ws.merge_cells("A1:I1")
    ws["A1"] = f"CÓMPUTOS MÉTRICOS - CORREDOR: {corridor_name}"
    ws["A1"].font = Font(bold=True, size=14)
    ws["A1"].alignment = Alignment(horizontal="center")

    header_fill = PatternFill(start_color="1F4E79", end_color="1F4E79", fill_type="solid")
    header_font = Font(color="FFFFFF", bold=True, size=10)

    headers = [
        "Estación Inicial", "Estación Final", "Distancia (m)",
        "Área Corte m²", "Área Relleno m²",
        "Vol. Corte m³", "Vol. Relleno m³",
        "Vol. Neto m³", "Balance Acum. m³"
    ]

    for col, header in enumerate(headers, 1):
        cell = ws.cell(row=2, column=col, value=header)
        cell.fill = header_fill
        cell.font = header_font
        cell.alignment = Alignment(horizontal="center")

    cumulative = 0.0
    for row_idx, v in enumerate(volumes, 3):
        cumulative += v["netVolume"]
        row_data = [
            f"0+{v['startStation']:07.2f}",
            f"0+{v['endStation']:07.2f}",
            v["distance"],
            v["avgCutArea"],
            v["avgFillArea"],
            v["cutVolume"],
            v["fillVolume"],
            v["netVolume"],
            round(cumulative, 2)
        ]
        for col, val in enumerate(row_data, 1):
            ws.cell(row=row_idx, column=col, value=val)

    # Summary
    summary_row = len(volumes) + 4
    ws.cell(row=summary_row, column=1, value="RESUMEN").font = Font(bold=True)
    ws.cell(row=summary_row + 1, column=1, value="Corte Total m³")
    ws.cell(row=summary_row + 1, column=2, value=mass_haul["totalCutM3"])
    ws.cell(row=summary_row + 2, column=1, value="Relleno Total m³")
    ws.cell(row=summary_row + 2, column=2, value=mass_haul["totalFillM3"])
    ws.cell(row=summary_row + 3, column=1, value="Balance Neto m³")
    ws.cell(row=summary_row + 3, column=2, value=mass_haul["netBalanceM3"])
    ws.cell(row=summary_row + 4, column=1, value=mass_haul["balanceType"]).font = Font(bold=True, color="FF0000")

    # Auto column width
    for col in range(1, 10):
        ws.column_dimensions[get_column_letter(col)].width = 18

    wb.save(output_path)
    return output_path


# ─────────────────────────────────────────────────────────────────────────────
# Dynamo entry point
# IN[0]: str - corridor name
# IN[1]: str - output format: "dict" | "volumes" | "mass_haul"
# OUT: computed result
# ─────────────────────────────────────────────────────────────────────────────
if "IN" in dir():
    _corridor_name = IN[0] if len(IN) > 0 else ""
    _output_format = IN[1] if len(IN) > 1 else "volumes"

    if not _corridor_name:
        OUT = get_all_corridors()
    elif _output_format == "mass_haul":
        OUT = generate_mass_haul_diagram(_corridor_name)
    else:
        OUT = calculate_earthwork_volumes(_corridor_name)
