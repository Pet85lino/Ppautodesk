"""
pipe_network_analysis.py
========================
Dynamo CPython script for Civil 3D pipe network analysis against
Ecuadorian engineering standards (primarily INTERAGUA-SDS-2015 and INEN-2536).

Operations
----------
- extract_pipes_with_attributes : Get all pipe data as list of dicts
- check_minimum_cover_depth     : Flag pipes violating cover requirements
- calculate_hydraulic_capacity  : Manning's equation for capacity check
- find_violations               : Combined standards compliance check
- generate_summary_report       : Aggregate stats and violation list
- export_report_to_excel        : Write full report via openpyxl

Usage in Dynamo
---------------
    from pipe_network_analysis import (
        extract_pipes_with_attributes,
        find_violations,
        generate_summary_report,
        export_report_to_excel,
    )
    pipes   = extract_pipes_with_attributes(session.active_document)
    report  = generate_summary_report(pipes, standard="INTERAGUA-SDS-2015")
    export_report_to_excel(pipes, report, output_path=r"C:\\reports\\pipes.xlsx")
"""

from __future__ import annotations

import math
import os
import traceback
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

# ---------------------------------------------------------------------------
# CLR bootstrap
# ---------------------------------------------------------------------------

try:
    import clr  # type: ignore

    for _asm in [
        "AcMgd", "AcDbMgd", "AcCoreMgd",
        "Autodesk.Civil.ApplicationServices",
        "Autodesk.Civil.DatabaseServices",
        "AeccXUiPipe",
        "AeccXUiLand",
    ]:
        try:
            clr.AddReference(_asm)
        except Exception:
            pass
    _CLR_OK = True
except ImportError:
    _CLR_OK = False


# ---------------------------------------------------------------------------
# Ecuadorian standard limits
# ---------------------------------------------------------------------------

STANDARDS: Dict[str, Dict[str, Any]] = {
    "INTERAGUA-SDS-2015": {
        "description":          "Interagua Sanitary Design Standard 2015",
        "min_diameter_mm":      200,
        "max_diameter_mm":      1500,
        "min_slope_pct":        0.50,
        "max_slope_pct":        10.00,
        "min_cover_road_m":     1.20,
        "min_cover_garden_m":   0.80,
        "max_cover_m":          5.00,
        "min_velocity_m_s":     0.60,
        "max_velocity_m_s":     3.00,
        "manning_n": {
            "concrete":   0.013,
            "pvc":        0.009,
            "hdpe":       0.010,
            "vitrified":  0.013,
            "clay":       0.015,
        },
        "allowed_materials":    ["concrete", "pvc", "hdpe", "vitrified clay"],
    },
    "INEN-2536": {
        "description":          "INEN 2536 Sanitary Sewer Systems",
        "min_diameter_mm":      200,
        "max_diameter_mm":      1200,
        "min_slope_pct":        0.50,
        "max_slope_pct":        15.00,
        "min_cover_road_m":     1.00,
        "min_cover_garden_m":   0.60,
        "max_cover_m":          6.00,
        "min_velocity_m_s":     0.60,
        "max_velocity_m_s":     4.50,
        "manning_n": {
            "concrete":   0.013,
            "pvc":        0.009,
            "hdpe":       0.010,
        },
        "allowed_materials":    ["concrete", "pvc", "hdpe"],
    },
    "EX-IEOS-1992": {
        "description":          "Ex-IEOS Sanitary and Storm Design Manual 1992",
        "min_diameter_mm":      200,
        "max_diameter_mm":      2000,
        "min_slope_pct":        0.30,
        "max_slope_pct":        20.00,
        "min_cover_road_m":     1.00,
        "min_cover_garden_m":   0.70,
        "max_cover_m":          8.00,
        "min_velocity_m_s":     0.45,
        "max_velocity_m_s":     5.00,
        "manning_n": {
            "concrete":   0.015,
            "pvc":        0.011,
        },
        "allowed_materials":    ["concrete", "pvc"],
    },
}

DEFAULT_STANDARD = "INTERAGUA-SDS-2015"


# ---------------------------------------------------------------------------
# PipeRecord type
# ---------------------------------------------------------------------------

PipeRecord = Dict[str, Any]
"""
Keys produced by extract_pipes_with_attributes:
    network_name, pipe_name, handle, start_structure, end_structure,
    length_m, diameter_mm, material, slope_pct, start_invert_m,
    end_invert_m, start_cover_m, end_cover_m, min_cover_m,
    shape (circular|box|...), roughness_n
"""


# ---------------------------------------------------------------------------
# 1. Extract pipes with attributes
# ---------------------------------------------------------------------------

def extract_pipes_with_attributes(civil_doc) -> List[PipeRecord]:
    """
    Return all pipes in every network in the document as a list of dicts.

    Attributes extracted per pipe
    ------------------------------
    network_name, pipe_name, handle, start_structure, end_structure,
    length_m, diameter_mm (inner), material, slope_pct,
    start_invert_m, end_invert_m, start_cover_m, end_cover_m,
    min_cover_m, shape, roughness_n (Manning)

    Parameters
    ----------
    civil_doc : CivilDocument

    Returns
    -------
    list of dict
    """
    _assert_clr()
    try:
        import Autodesk.AutoCAD.DatabaseServices as AcDb          # type: ignore
        from Autodesk.Civil.DatabaseServices import (             # type: ignore
            Network, Pipe, PartDataField
        )

        records: List[PipeRecord] = []

        net_ids = civil_doc.GetPipeNetworkIds()

        with AcDb.Transaction() as tr:
            for net_id in net_ids:
                net = tr.GetObject(net_id, AcDb.OpenMode.ForRead)
                if not isinstance(net, Network):
                    continue
                net_name = str(net.Name)

                for pipe_id in net.GetPipeIds():
                    try:
                        pipe = tr.GetObject(pipe_id, AcDb.OpenMode.ForRead)
                        if not isinstance(pipe, Pipe):
                            continue
                        rec = _extract_pipe_record(pipe, net_name)
                        records.append(rec)
                    except Exception as exc:
                        records.append({
                            "network_name": net_name,
                            "pipe_name":    "UNKNOWN",
                            "error":        str(exc),
                        })

            tr.Commit()

        return records

    except Exception as exc:
        raise RuntimeError(
            f"extract_pipes_with_attributes failed: {exc}\n"
            f"{traceback.format_exc()}"
        ) from exc


def _extract_pipe_record(pipe, net_name: str) -> PipeRecord:
    """Build a PipeRecord dict from a Civil 3D Pipe object."""
    def safe_float(val, default=0.0) -> float:
        try:
            return float(val)
        except Exception:
            return default

    def safe_str(val, default="") -> str:
        try:
            return str(val) if val is not None else default
        except Exception:
            return default

    diameter_m  = safe_float(getattr(pipe, "InnerDiameterOrWidth", 0.0))
    diameter_mm = diameter_m * 1000.0

    length_m       = safe_float(getattr(pipe, "Length2DCenterToCenter", 0.0))
    slope_ratio    = safe_float(getattr(pipe, "Slope", 0.0))
    slope_pct      = abs(slope_ratio) * 100.0

    start_invert   = safe_float(getattr(pipe, "StartPoint",  None) and
                                getattr(pipe.StartPoint, "Z", 0.0), 0.0)
    end_invert     = safe_float(getattr(pipe, "EndPoint",    None) and
                                getattr(pipe.EndPoint, "Z", 0.0), 0.0)

    # Cover depths: Civil 3D stores these as properties on the pipe.
    start_cover    = safe_float(getattr(pipe, "StartCoverDepth", None), -1.0)
    end_cover      = safe_float(getattr(pipe, "EndCoverDepth",   None), -1.0)
    min_cover      = min(v for v in [start_cover, end_cover] if v >= 0) \
                     if any(v >= 0 for v in [start_cover, end_cover]) else -1.0

    material       = safe_str(getattr(pipe, "PartDescription", "Concrete"))
    shape          = safe_str(getattr(pipe, "CrossSectionShape", "Circular"))

    # Infer Manning n from material string
    mat_lower = material.lower()
    roughness = 0.013  # default: concrete
    for key, n in {"pvc": 0.009, "hdpe": 0.010, "vitrified": 0.013,
                   "clay": 0.015, "plastic": 0.009}.items():
        if key in mat_lower:
            roughness = n
            break

    start_struct = safe_str(getattr(pipe, "StartStructureId", ""))
    end_struct   = safe_str(getattr(pipe, "EndStructureId", ""))

    return {
        "network_name":    net_name,
        "pipe_name":       safe_str(getattr(pipe, "Name", "")),
        "handle":          safe_str(getattr(pipe.ObjectId, "Handle", "")),
        "start_structure": start_struct,
        "end_structure":   end_struct,
        "length_m":        round(length_m, 3),
        "diameter_mm":     round(diameter_mm, 1),
        "material":        material,
        "slope_pct":       round(slope_pct, 4),
        "start_invert_m":  round(start_invert, 3),
        "end_invert_m":    round(end_invert, 3),
        "start_cover_m":   round(start_cover, 3),
        "end_cover_m":     round(end_cover, 3),
        "min_cover_m":     round(min_cover, 3),
        "shape":           shape,
        "roughness_n":     roughness,
    }


# ---------------------------------------------------------------------------
# 2. Check minimum cover depth
# ---------------------------------------------------------------------------

def check_minimum_cover_depth(
    pipes: List[PipeRecord],
    standard: str = DEFAULT_STANDARD,
    area_type: str = "road",  # "road" | "garden"
) -> List[PipeRecord]:
    """
    Return a filtered list of pipes that violate minimum cover depth.

    Parameters
    ----------
    pipes      : output of extract_pipes_with_attributes
    standard   : standard key from STANDARDS dict
    area_type  : "road" for under-road coverage, "garden" for landscaped areas

    Returns
    -------
    list of dicts with an added key "cover_violation_m" (shortfall in metres)
    """
    std = _get_standard(standard)
    min_key = "min_cover_road_m" if area_type == "road" else "min_cover_garden_m"
    min_cover = float(std[min_key])
    max_cover = float(std["max_cover_m"])

    violations: List[PipeRecord] = []
    for pipe in pipes:
        cover = pipe.get("min_cover_m", -1.0)
        if cover < 0:
            continue  # data not available

        if cover < min_cover:
            rec = dict(pipe)
            rec["cover_violation_type"] = "too_shallow"
            rec["cover_violation_m"]    = round(min_cover - cover, 3)
            rec["required_cover_m"]     = min_cover
            violations.append(rec)
        elif cover > max_cover:
            rec = dict(pipe)
            rec["cover_violation_type"] = "too_deep"
            rec["cover_violation_m"]    = round(cover - max_cover, 3)
            rec["required_cover_m"]     = max_cover
            violations.append(rec)

    return violations


# ---------------------------------------------------------------------------
# 3. Calculate hydraulic capacity
# ---------------------------------------------------------------------------

def calculate_hydraulic_capacity(
    pipes: List[PipeRecord],
    standard: str = DEFAULT_STANDARD,
) -> List[Dict[str, Any]]:
    """
    Compute full-bore hydraulic capacity and velocity using Manning's equation
    for each pipe.  Returns a new list enriched with hydraulic attributes.

    Manning's equation (SI):
        Q = (1/n) * A * R^(2/3) * S^(1/2)

    where
        n  = Manning roughness coefficient
        A  = cross-sectional area of flow  [m²]
        R  = hydraulic radius  A / P  [m]
        S  = slope  [m/m]  (dimensionless)

    Parameters
    ----------
    pipes    : list of PipeRecord dicts
    standard : Ecuadorian standard key

    Returns
    -------
    list of dicts, each containing all original fields plus:
        "capacity_m3_s", "velocity_m_s", "velocity_ok", "capacity_ok"
    """
    std = _get_standard(standard)
    v_min = float(std["min_velocity_m_s"])
    v_max = float(std["max_velocity_m_s"])

    results: List[Dict[str, Any]] = []

    for pipe in pipes:
        rec = dict(pipe)

        diameter_m = pipe.get("diameter_mm", 0.0) / 1000.0
        slope_pct  = pipe.get("slope_pct", 0.0)
        n          = pipe.get("roughness_n", 0.013)

        if diameter_m <= 0 or slope_pct <= 0:
            rec["capacity_m3_s"] = None
            rec["velocity_m_s"]  = None
            rec["velocity_ok"]   = False
            rec["capacity_ok"]   = False
            results.append(rec)
            continue

        slope_ratio = slope_pct / 100.0

        # Circular section, full bore
        area   = math.pi * (diameter_m / 2.0) ** 2            # m²
        perim  = math.pi * diameter_m                          # m
        r_h    = area / perim                                  # hydraulic radius m

        capacity   = (1.0 / n) * area * (r_h ** (2.0 / 3.0)) * math.sqrt(slope_ratio)
        velocity   = capacity / area if area > 0 else 0.0

        rec["capacity_m3_s"] = round(capacity, 6)
        rec["velocity_m_s"]  = round(velocity, 4)
        rec["velocity_ok"]   = v_min <= velocity <= v_max
        rec["capacity_ok"]   = velocity >= v_min

        results.append(rec)

    return results


# ---------------------------------------------------------------------------
# 4. Find all violations against a standard
# ---------------------------------------------------------------------------

def find_violations(
    pipes: List[PipeRecord],
    standard: str = DEFAULT_STANDARD,
    area_type: str = "road",
) -> List[Dict[str, Any]]:
    """
    Perform a full compliance check of all pipes against the named standard.

    Checks performed
    ----------------
    1. Diameter within allowed range
    2. Slope within allowed range
    3. Minimum cover depth
    4. Minimum/maximum flow velocity (Manning)
    5. Material allowed by standard

    Parameters
    ----------
    pipes     : list of PipeRecord from extract_pipes_with_attributes
    standard  : standard key (default INTERAGUA-SDS-2015)
    area_type : "road" | "garden"

    Returns
    -------
    list of dicts; each has all original pipe fields plus:
        "violations" : list of violation strings
        "is_compliant": bool
    """
    std      = _get_standard(standard)
    hydro    = {r["pipe_name"]: r
                for r in calculate_hydraulic_capacity(pipes, standard)}
    cover_vio = {p["pipe_name"]: p
                 for p in check_minimum_cover_depth(pipes, standard, area_type)}

    min_dia    = float(std["min_diameter_mm"])
    max_dia    = float(std["max_diameter_mm"])
    min_slope  = float(std["min_slope_pct"])
    max_slope  = float(std["max_slope_pct"])
    v_min      = float(std["min_velocity_m_s"])
    v_max      = float(std["max_velocity_m_s"])
    allowed_mat = [m.lower() for m in std.get("allowed_materials", [])]

    report: List[Dict[str, Any]] = []

    for pipe in pipes:
        rec        = dict(pipe)
        violations: List[str] = []
        pname      = pipe.get("pipe_name", "")

        # 1. Diameter
        dia = pipe.get("diameter_mm", 0.0)
        if dia < min_dia:
            violations.append(
                f"Diameter {dia:.1f} mm < minimum {min_dia:.0f} mm"
            )
        elif dia > max_dia:
            violations.append(
                f"Diameter {dia:.1f} mm > maximum {max_dia:.0f} mm"
            )

        # 2. Slope
        slope = pipe.get("slope_pct", 0.0)
        if slope < min_slope:
            violations.append(
                f"Slope {slope:.3f}% < minimum {min_slope:.2f}%"
            )
        elif slope > max_slope:
            violations.append(
                f"Slope {slope:.3f}% > maximum {max_slope:.2f}%"
            )

        # 3. Cover depth
        if pname in cover_vio:
            cv = cover_vio[pname]
            violations.append(
                f"Cover depth {pipe.get('min_cover_m', '?'):.2f} m — "
                f"{cv['cover_violation_type']} by {cv['cover_violation_m']:.3f} m"
            )

        # 4. Velocity
        h = hydro.get(pname, {})
        v = h.get("velocity_m_s")
        if v is not None:
            if v < v_min:
                violations.append(
                    f"Velocity {v:.3f} m/s < minimum {v_min:.2f} m/s (self-cleaning)"
                )
            elif v > v_max:
                violations.append(
                    f"Velocity {v:.3f} m/s > maximum {v_max:.2f} m/s (erosion risk)"
                )

        # 5. Material
        mat = pipe.get("material", "").lower()
        if allowed_mat and not any(a in mat for a in allowed_mat):
            violations.append(
                f"Material '{pipe.get('material')}' not in allowed list: "
                f"{std.get('allowed_materials')}"
            )

        rec["violations"]    = violations
        rec["is_compliant"]  = len(violations) == 0
        rec["standard"]      = standard
        if v is not None:
            rec["velocity_m_s"]  = h.get("velocity_m_s")
            rec["capacity_m3_s"] = h.get("capacity_m3_s")

        report.append(rec)

    return report


# ---------------------------------------------------------------------------
# 5. Generate summary report
# ---------------------------------------------------------------------------

def generate_summary_report(
    pipes: List[PipeRecord],
    standard: str = DEFAULT_STANDARD,
    area_type: str = "road",
) -> Dict[str, Any]:
    """
    Generate a summary statistics dict for all pipes against the standard.

    Returns
    -------
    dict with keys:
        "standard", "total_pipes", "total_length_m",
        "compliant_pipes", "non_compliant_pipes",
        "compliance_rate_pct",
        "violation_breakdown" (dict: violation_type → count),
        "pipes_by_diameter_mm" (dict: diameter → count),
        "generated_at" (ISO datetime string),
        "violations" (list of non-compliant pipe dicts from find_violations)
    """
    violation_records = find_violations(pipes, standard, area_type)

    total      = len(violation_records)
    compliant  = sum(1 for r in violation_records if r["is_compliant"])
    non_comp   = total - compliant
    comp_rate  = (compliant / total * 100.0) if total > 0 else 0.0
    total_len  = sum(r.get("length_m", 0.0) for r in violation_records)

    # Violation type breakdown
    breakdown: Dict[str, int] = {}
    for rec in violation_records:
        for v in rec.get("violations", []):
            key = v.split(" ")[0].capitalize()  # first word: "Diameter", "Slope", ...
            breakdown[key] = breakdown.get(key, 0) + 1

    # Diameter distribution
    dia_dist: Dict[str, int] = {}
    for rec in violation_records:
        dia = rec.get("diameter_mm", 0)
        key = f"{int(round(dia))} mm"
        dia_dist[key] = dia_dist.get(key, 0) + 1

    return {
        "standard":              standard,
        "total_pipes":           total,
        "total_length_m":        round(total_len, 2),
        "compliant_pipes":       compliant,
        "non_compliant_pipes":   non_comp,
        "compliance_rate_pct":   round(comp_rate, 1),
        "violation_breakdown":   breakdown,
        "pipes_by_diameter_mm":  dia_dist,
        "generated_at":          datetime.utcnow().isoformat() + "Z",
        "violations":            [r for r in violation_records if not r["is_compliant"]],
    }


# ---------------------------------------------------------------------------
# 6. Export to Excel via openpyxl
# ---------------------------------------------------------------------------

def export_report_to_excel(
    pipes: List[PipeRecord],
    summary: Optional[Dict[str, Any]] = None,
    output_path: str = "pipe_network_report.xlsx",
    standard: str = DEFAULT_STANDARD,
) -> str:
    """
    Write a formatted Excel workbook with pipe data and violations.

    Sheets
    ------
    1. Summary       – key metrics and violation breakdown
    2. All Pipes     – full attribute table
    3. Violations    – non-compliant pipes only

    Parameters
    ----------
    pipes       : list of PipeRecord from extract_pipes_with_attributes
    summary     : optional dict from generate_summary_report; auto-generated if None
    output_path : str – path to the .xlsx file
    standard    : standard key used for the analysis

    Returns
    -------
    str – absolute path of the written file
    """
    try:
        import openpyxl                             # type: ignore
        from openpyxl.styles import (              # type: ignore
            Font, PatternFill, Alignment, Border, Side
        )
        from openpyxl.utils import get_column_letter  # type: ignore
    except ImportError:
        raise ImportError(
            "openpyxl is required for Excel export. "
            "Install it with: pip install openpyxl"
        )

    if summary is None:
        summary = generate_summary_report(pipes, standard)

    violation_set = {r["pipe_name"] for r in summary.get("violations", [])}
    full_records  = find_violations(pipes, standard)

    wb = openpyxl.Workbook()

    # ── Sheet 1: Summary ───────────────────────────────────────────────────
    ws_sum = wb.active
    ws_sum.title = "Summary"

    header_fill = PatternFill("solid", fgColor="1F497D")
    header_font = Font(color="FFFFFF", bold=True, size=11)

    def write_row(ws, row: int, col: int, label: str, value: Any,
                  label_bold: bool = True):
        lc = ws.cell(row=row, column=col, value=label)
        vc = ws.cell(row=row, column=col + 1, value=value)
        if label_bold:
            lc.font = Font(bold=True)
        return lc, vc

    ws_sum.column_dimensions["A"].width = 30
    ws_sum.column_dimensions["B"].width = 20

    ws_sum.cell(1, 1, "Pipe Network Analysis Report").font = Font(bold=True, size=14)
    ws_sum.cell(2, 1, f"Standard: {summary['standard']}").font = Font(italic=True)
    ws_sum.cell(3, 1, f"Generated: {summary['generated_at']}")

    r = 5
    for label, key in [
        ("Total Pipes",           "total_pipes"),
        ("Total Length (m)",      "total_length_m"),
        ("Compliant Pipes",       "compliant_pipes"),
        ("Non-Compliant Pipes",   "non_compliant_pipes"),
        ("Compliance Rate (%)",   "compliance_rate_pct"),
    ]:
        write_row(ws_sum, r, 1, label, summary.get(key, ""))
        r += 1

    r += 1
    ws_sum.cell(r, 1, "Violation Breakdown").font = Font(bold=True)
    r += 1
    for vtype, count in summary.get("violation_breakdown", {}).items():
        write_row(ws_sum, r, 1, vtype, count)
        r += 1

    r += 1
    ws_sum.cell(r, 1, "Diameter Distribution").font = Font(bold=True)
    r += 1
    for dia_key, count in summary.get("pipes_by_diameter_mm", {}).items():
        write_row(ws_sum, r, 1, dia_key, count)
        r += 1

    # ── Sheet 2: All Pipes ─────────────────────────────────────────────────
    ws_pipes = wb.create_sheet("All Pipes")
    PIPE_COLS = [
        "network_name", "pipe_name", "length_m", "diameter_mm",
        "material", "slope_pct", "start_invert_m", "end_invert_m",
        "min_cover_m", "velocity_m_s", "capacity_m3_s",
        "is_compliant", "violations",
    ]

    for col_idx, col_name in enumerate(PIPE_COLS, start=1):
        cell = ws_pipes.cell(1, col_idx, col_name.replace("_", " ").title())
        cell.font = header_font
        cell.fill = header_fill
        cell.alignment = Alignment(horizontal="center")
        ws_pipes.column_dimensions[get_column_letter(col_idx)].width = 18

    red_fill   = PatternFill("solid", fgColor="FFCCCC")
    green_fill = PatternFill("solid", fgColor="CCFFCC")

    for row_idx, rec in enumerate(full_records, start=2):
        for col_idx, col_name in enumerate(PIPE_COLS, start=1):
            val = rec.get(col_name, "")
            if isinstance(val, list):
                val = "; ".join(val) if val else "OK"
            elif isinstance(val, bool):
                val = "Yes" if val else "No"
            elif isinstance(val, float):
                val = round(val, 4)
            cell = ws_pipes.cell(row_idx, col_idx, val)

            if col_name == "is_compliant":
                cell.fill = green_fill if rec.get("is_compliant") else red_fill

    # Freeze header row
    ws_pipes.freeze_panes = "A2"

    # ── Sheet 3: Violations ────────────────────────────────────────────────
    ws_viol = wb.create_sheet("Violations")
    for col_idx, col_name in enumerate(PIPE_COLS, start=1):
        cell = ws_viol.cell(1, col_idx, col_name.replace("_", " ").title())
        cell.font = header_font
        cell.fill = header_fill
        cell.alignment = Alignment(horizontal="center")
        ws_viol.column_dimensions[get_column_letter(col_idx)].width = 18

    viol_rows = [r for r in full_records if not r.get("is_compliant", True)]
    for row_idx, rec in enumerate(viol_rows, start=2):
        for col_idx, col_name in enumerate(PIPE_COLS, start=1):
            val = rec.get(col_name, "")
            if isinstance(val, list):
                val = "; ".join(val) if val else "OK"
            elif isinstance(val, bool):
                val = "Yes" if val else "No"
            elif isinstance(val, float):
                val = round(val, 4)
            ws_viol.cell(row_idx, col_idx, val).fill = red_fill

    ws_viol.freeze_panes = "A2"

    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    wb.save(output_path)

    return os.path.abspath(output_path)


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _assert_clr() -> None:
    if not _CLR_OK:
        raise RuntimeError(
            "CLR / Civil 3D assemblies are not available. "
            "This script must run inside the Dynamo CPython engine."
        )


def _get_standard(key: str) -> Dict[str, Any]:
    std = STANDARDS.get(key)
    if std is None:
        available = list(STANDARDS.keys())
        raise KeyError(
            f"Unknown standard '{key}'. Available: {available}"
        )
    return std
