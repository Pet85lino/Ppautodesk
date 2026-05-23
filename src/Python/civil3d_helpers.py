"""
civil3d_helpers.py
==================
Helper library for Dynamo (CPython engine) targeting Autodesk Civil 3D 2025/2026/2027.

Usage inside a Dynamo Python node
----------------------------------
    import sys
    sys.path.insert(0, r"C:\\path\\to\\civil3d_helpers.py parent dir")
    from civil3d_helpers import Civil3DSession, UnitConverter, get_objects_by_type

All public functions return plain Python objects (lists, dicts, numbers) so they
can be wired directly to Dynamo output ports.

Requirements
------------
- Dynamo 2.17+ with CPython 3 engine (IronPython is NOT supported)
- Civil 3D 2025, 2026 or 2027 running as the host application
"""

from __future__ import annotations

import sys
import math
import traceback
from typing import Any, Dict, List, Optional, Tuple

# ---------------------------------------------------------------------------
# clr / .NET bootstrap
# ---------------------------------------------------------------------------

try:
    import clr  # type: ignore
    _CLR_AVAILABLE = True
except ImportError:
    _CLR_AVAILABLE = False

# Version candidates newest-first so the first match wins.
_CIVIL3D_VERSION_YEARS: List[int] = [2027, 2026, 2025]

# Mapping from version year → internal AutoCAD major version
_YEAR_TO_ACAD_VER: Dict[int, str] = {
    2027: "27.0",
    2026: "26.0",
    2025: "25.0",
}

# Assemblies required for Civil 3D .NET access
_REQUIRED_ASSEMBLIES: List[str] = [
    "AcMgd",
    "AcDbMgd",
    "AcCoreMgd",
    "Autodesk.Civil.ApplicationServices",
    "Autodesk.Civil.DatabaseServices",
    "AeccXUiLand",
]

_OPTIONAL_ASSEMBLIES: List[str] = [
    "AeccXUiPipe",
    "AeccXUiRoadway",
    "AeccXUiSurvey",
    "AeccXUiGrading",
    "Autodesk.Civil.DatabaseServices.Styles",
    "Autodesk.Civil.Runtime",
]


def _add_reference_safe(assembly_name: str) -> bool:
    """Add a CLR reference, returning True on success."""
    if not _CLR_AVAILABLE:
        return False
    try:
        clr.AddReference(assembly_name)
        return True
    except Exception:
        return False


class Civil3DSession:
    """
    Manages the Civil 3D .NET session inside a Dynamo Python node.

    Example
    -------
    >>> session = Civil3DSession()
    >>> doc = session.active_document
    >>> db  = session.database
    """

    def __init__(self) -> None:
        self._loaded: bool = False
        self._errors: List[str] = []
        self._year: Optional[int] = None

        self._bootstrap()

    # ------------------------------------------------------------------
    # Bootstrap
    # ------------------------------------------------------------------

    def _bootstrap(self) -> None:
        if not _CLR_AVAILABLE:
            self._errors.append(
                "clr module not available. "
                "Ensure this script runs inside the Dynamo CPython engine."
            )
            return

        # Try each version year until assemblies load successfully.
        for year in _CIVIL3D_VERSION_YEARS:
            if self._try_load_year(year):
                self._year = year
                self._loaded = True
                return

        self._errors.append(
            f"Could not load Civil 3D assemblies for versions "
            f"{_CIVIL3D_VERSION_YEARS}. "
            "Make sure Civil 3D is installed and running."
        )

    def _try_load_year(self, year: int) -> bool:
        # Required assemblies must ALL succeed.
        for asm in _REQUIRED_ASSEMBLIES:
            if not _add_reference_safe(asm):
                return False

        # Optional assemblies – failures are silent.
        for asm in _OPTIONAL_ASSEMBLIES:
            _add_reference_safe(asm)

        return True

    # ------------------------------------------------------------------
    # Properties
    # ------------------------------------------------------------------

    @property
    def is_loaded(self) -> bool:
        return self._loaded

    @property
    def version_year(self) -> Optional[int]:
        return self._year

    @property
    def errors(self) -> List[str]:
        return list(self._errors)

    @property
    def active_document(self):
        """
        Returns the active CivilDocument from the running Civil 3D session.
        Raises RuntimeError if Civil 3D is not available.
        """
        self._assert_loaded()
        try:
            import Autodesk.Civil.ApplicationServices as CivilApp  # type: ignore
            import Autodesk.AutoCAD.ApplicationServices as AcApp    # type: ignore

            acad_doc = AcApp.Application.DocumentManager.MdiActiveDocument
            if acad_doc is None:
                raise RuntimeError("No active AutoCAD document found.")

            civil_doc = CivilApp.CivilApplication.ActiveDocument
            if civil_doc is None:
                raise RuntimeError(
                    "Active document is not a Civil 3D document."
                )
            return civil_doc
        except ImportError as exc:
            raise RuntimeError(
                f"Civil 3D application services not importable: {exc}"
            ) from exc

    @property
    def database(self):
        """Returns the AutoCAD Database for the active document."""
        self._assert_loaded()
        try:
            import Autodesk.AutoCAD.ApplicationServices as AcApp  # type: ignore

            doc = AcApp.Application.DocumentManager.MdiActiveDocument
            if doc is None:
                raise RuntimeError("No active AutoCAD document.")
            return doc.Database
        except ImportError as exc:
            raise RuntimeError(
                f"AutoCAD managed services not importable: {exc}"
            ) from exc

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _assert_loaded(self) -> None:
        if not self._loaded:
            raise RuntimeError(
                "Civil 3D session is not initialised. "
                f"Errors: {'; '.join(self._errors)}"
            )

    def __repr__(self) -> str:  # noqa: D105
        status = f"Civil 3D {self._year}" if self._loaded else "NOT LOADED"
        return f"Civil3DSession({status})"


# ---------------------------------------------------------------------------
# Object retrieval helpers
# ---------------------------------------------------------------------------

def get_alignments(civil_doc) -> List[Any]:
    """
    Return all Alignment objects in the Civil 3D document.

    Parameters
    ----------
    civil_doc:
        CivilDocument obtained from Civil3DSession.active_document.

    Returns
    -------
    list of Autodesk.Civil.DatabaseServices.Alignment
    """
    try:
        from Autodesk.Civil.DatabaseServices import Alignment  # type: ignore
        import Autodesk.AutoCAD.DatabaseServices as AcDb       # type: ignore

        db = civil_doc.Database
        alignments: List[Any] = []

        with AcDb.Transaction() as tr:
            civil_doc.GetAlignmentIds()  # force refresh
            ids = civil_doc.GetAlignmentIds()
            for obj_id in ids:
                obj = tr.GetObject(obj_id, AcDb.OpenMode.ForRead)
                if isinstance(obj, Alignment):
                    alignments.append(obj)
            tr.Commit()

        return alignments
    except Exception as exc:
        raise RuntimeError(
            f"Failed to retrieve alignments: {exc}\n{traceback.format_exc()}"
        ) from exc


def get_alignment_by_name(civil_doc, name: str) -> Any:
    """
    Return a single Alignment by name (case-insensitive).

    Raises
    ------
    KeyError
        If no alignment with the given name exists.
    """
    name_lower = name.strip().lower()
    for align in get_alignments(civil_doc):
        if align.Name.lower() == name_lower:
            return align
    raise KeyError(
        f"Alignment '{name}' not found in the active document."
    )


def get_surfaces(civil_doc) -> List[Any]:
    """Return all TIN/Grid surface objects in the document."""
    try:
        from Autodesk.Civil.DatabaseServices import TinSurface, GridSurface  # type: ignore
        import Autodesk.AutoCAD.DatabaseServices as AcDb                      # type: ignore

        db = civil_doc.Database
        surfaces: List[Any] = []

        with AcDb.Transaction() as tr:
            ids = civil_doc.GetSurfaceIds()
            for obj_id in ids:
                obj = tr.GetObject(obj_id, AcDb.OpenMode.ForRead)
                if isinstance(obj, (TinSurface, GridSurface)):
                    surfaces.append(obj)
            tr.Commit()

        return surfaces
    except Exception as exc:
        raise RuntimeError(
            f"Failed to retrieve surfaces: {exc}\n{traceback.format_exc()}"
        ) from exc


def get_surface_by_name(civil_doc, name: str) -> Any:
    """Return a surface by name (case-insensitive)."""
    name_lower = name.strip().lower()
    for surf in get_surfaces(civil_doc):
        if surf.Name.lower() == name_lower:
            return surf
    raise KeyError(f"Surface '{name}' not found.")


def get_pipe_networks(civil_doc) -> List[Any]:
    """Return all pipe network objects in the document."""
    try:
        from Autodesk.Civil.DatabaseServices import Network  # type: ignore
        import Autodesk.AutoCAD.DatabaseServices as AcDb     # type: ignore

        networks: List[Any] = []

        with AcDb.Transaction() as tr:
            ids = civil_doc.GetPipeNetworkIds()
            for obj_id in ids:
                obj = tr.GetObject(obj_id, AcDb.OpenMode.ForRead)
                if isinstance(obj, Network):
                    networks.append(obj)
            tr.Commit()

        return networks
    except Exception as exc:
        raise RuntimeError(
            f"Failed to retrieve pipe networks: {exc}\n{traceback.format_exc()}"
        ) from exc


def get_pipe_network_by_name(civil_doc, name: str) -> Any:
    """Return a pipe network by name (case-insensitive)."""
    name_lower = name.strip().lower()
    for net in get_pipe_networks(civil_doc):
        if net.Name.lower() == name_lower:
            return net
    raise KeyError(f"Pipe network '{name}' not found.")


def get_objects_by_type(civil_doc, obj_type_name: str) -> List[Any]:
    """
    Generic object collector by Civil 3D type name string.

    Supported type_name values
    --------------------------
    "alignment", "surface", "tinSurface", "corridor",
    "pipeNetwork", "structure", "pipe", "profile", "assembly"

    Returns
    -------
    list of Civil 3D objects
    """
    t = obj_type_name.strip().lower()
    dispatch: Dict[str, Any] = {
        "alignment":   get_alignments,
        "surface":     get_surfaces,
        "tinsurface":  get_surfaces,
        "pipenetwork": get_pipe_networks,
        "pipe":        lambda d: _get_pipes_all(d),
        "structure":   lambda d: _get_structures_all(d),
    }

    fn = dispatch.get(t)
    if fn is None:
        raise ValueError(
            f"Unsupported object type '{obj_type_name}'. "
            f"Valid values: {sorted(dispatch.keys())}"
        )
    return fn(civil_doc)


def _get_pipes_all(civil_doc) -> List[Any]:
    """Aggregate all pipes across every network in the document."""
    pipes: List[Any] = []
    for net in get_pipe_networks(civil_doc):
        try:
            from Autodesk.Civil.DatabaseServices import Pipe  # type: ignore
            import Autodesk.AutoCAD.DatabaseServices as AcDb  # type: ignore

            with AcDb.Transaction() as tr:
                for pid in net.GetPipeIds():
                    obj = tr.GetObject(pid, AcDb.OpenMode.ForRead)
                    if isinstance(obj, Pipe):
                        pipes.append(obj)
                tr.Commit()
        except Exception:
            pass
    return pipes


def _get_structures_all(civil_doc) -> List[Any]:
    """Aggregate all structures across every network in the document."""
    structures: List[Any] = []
    for net in get_pipe_networks(civil_doc):
        try:
            from Autodesk.Civil.DatabaseServices import Structure  # type: ignore
            import Autodesk.AutoCAD.DatabaseServices as AcDb        # type: ignore

            with AcDb.Transaction() as tr:
                for sid in net.GetStructureIds():
                    obj = tr.GetObject(sid, AcDb.OpenMode.ForRead)
                    if isinstance(obj, Structure):
                        structures.append(obj)
                tr.Commit()
        except Exception:
            pass
    return structures


# ---------------------------------------------------------------------------
# Unit conversion utilities  (metric – Ecuadorian standards)
# ---------------------------------------------------------------------------

class UnitConverter:
    """
    Unit conversion helpers for Civil 3D values (stored in drawing units,
    which are typically metres in Ecuadorian projects).
    """

    # Civil 3D stores slopes as ratio (e.g. 0.005 = 0.5%)
    @staticmethod
    def slope_ratio_to_percent(ratio: float) -> float:
        """Convert a Civil 3D slope ratio (0.005) to percentage (0.5)."""
        return ratio * 100.0

    @staticmethod
    def slope_percent_to_ratio(percent: float) -> float:
        """Convert percentage slope (0.5) to Civil 3D ratio (0.005)."""
        return percent / 100.0

    # Civil 3D stores pipe diameters in the drawing unit (metres).
    @staticmethod
    def m_to_mm(metres: float) -> float:
        return metres * 1000.0

    @staticmethod
    def mm_to_m(mm: float) -> float:
        return mm / 1000.0

    @staticmethod
    def m_to_cm(metres: float) -> float:
        return metres * 100.0

    @staticmethod
    def cm_to_m(cm: float) -> float:
        return cm / 100.0

    # Station formatting
    @staticmethod
    def station_to_km_m(station_m: float) -> str:
        """Format a station value (metres) as 'KM+MM.mmm'."""
        km = int(station_m // 1000)
        m  = station_m - km * 1000.0
        return f"{km:02d}+{m:06.3f}"

    @staticmethod
    def km_m_to_station(km_m_str: str) -> float:
        """
        Parse a station string 'KK+MMM.mmm' back to decimal metres.
        """
        km_m_str = km_m_str.strip().replace(" ", "")
        if "+" in km_m_str:
            parts = km_m_str.split("+", 1)
            km = float(parts[0])
            m  = float(parts[1])
        else:
            km = 0.0
            m  = float(km_m_str)
        return km * 1000.0 + m


# ---------------------------------------------------------------------------
# Coordinate transformation helpers
# ---------------------------------------------------------------------------

def wgs84_to_utm_zone17s(lat_deg: float, lon_deg: float) -> Tuple[float, float]:
    """
    Approximate conversion from WGS-84 geographic coordinates to
    UTM Zone 17S (EPSG:32717), which covers most of Ecuador.

    Parameters
    ----------
    lat_deg : float  (negative for South)
    lon_deg : float  (negative for West)

    Returns
    -------
    (easting_m, northing_m) tuple

    Note
    ----
    For production use, prefer pyproj / GDAL.  This is a simplified
    Karney-series approximation suitable for ±0.1 m accuracy.
    """
    # WGS-84 ellipsoid
    a   = 6_378_137.0           # semi-major axis  [m]
    f   = 1.0 / 298.257_223_563
    b   = a * (1.0 - f)
    e2  = 2.0 * f - f * f       # eccentricity²
    e   = math.sqrt(e2)

    k0  = 0.9996                # UTM scale factor
    E0  = 500_000.0             # false easting  [m]
    N0  = 10_000_000.0          # false northing [m] (Southern hemisphere)

    lon0 = math.radians(-78.0)  # Zone 17 central meridian
    lat  = math.radians(lat_deg)
    lon  = math.radians(lon_deg)

    N  = a / math.sqrt(1.0 - e2 * math.sin(lat) ** 2)
    T  = math.tan(lat) ** 2
    C  = (e2 / (1.0 - e2)) * math.cos(lat) ** 2
    A  = math.cos(lat) * (lon - lon0)

    M = a * (
        (1 - e2 / 4 - 3 * e2 ** 2 / 64 - 5 * e2 ** 3 / 256) * lat
        - (3 * e2 / 8 + 3 * e2 ** 2 / 32 + 45 * e2 ** 3 / 1024) * math.sin(2 * lat)
        + (15 * e2 ** 2 / 256 + 45 * e2 ** 3 / 1024) * math.sin(4 * lat)
        - (35 * e2 ** 3 / 3072) * math.sin(6 * lat)
    )

    easting = (
        k0 * N * (
            A
            + (1 - T + C) * A ** 3 / 6
            + (5 - 18 * T + T ** 2 + 72 * C - 58 * (e2 / (1 - e2))) * A ** 5 / 120
        )
        + E0
    )

    northing = (
        k0 * (
            M
            + N * math.tan(lat) * (
                A ** 2 / 2
                + (5 - T + 9 * C + 4 * C ** 2) * A ** 4 / 24
                + (61 - 58 * T + T ** 2 + 600 * C - 330 * (e2 / (1 - e2))) * A ** 6 / 720
            )
        )
        + N0
    )

    return (easting, northing)


def point3d_to_dict(point3d_obj) -> Dict[str, float]:
    """
    Convert an Autodesk.AutoCAD.Geometry.Point3d object to a plain dict.

    Returns
    -------
    {"x": ..., "y": ..., "z": ...}
    """
    try:
        return {
            "x": float(point3d_obj.X),
            "y": float(point3d_obj.Y),
            "z": float(point3d_obj.Z),
        }
    except AttributeError:
        raise TypeError(
            f"Expected a Point3d object, got {type(point3d_obj).__name__}"
        )


# ---------------------------------------------------------------------------
# Batch operation helper
# ---------------------------------------------------------------------------

def batch_apply(
    items: List[Any],
    func,
    *,
    stop_on_error: bool = False,
    error_value: Any = None,
) -> List[Any]:
    """
    Apply *func* to each item in *items* and return a list of results.

    Parameters
    ----------
    items         : iterable of objects to process
    func          : callable(item) → result
    stop_on_error : if True, re-raises the first exception
    error_value   : value substituted for failed items (default None)

    Returns
    -------
    list with one element per input item (None / error_value on failure)

    Example
    -------
    >>> names = batch_apply(pipes, lambda p: p.Name)
    """
    results: List[Any] = []
    for item in items:
        try:
            results.append(func(item))
        except Exception as exc:
            if stop_on_error:
                raise
            results.append(error_value)
    return results


# ---------------------------------------------------------------------------
# Diagnostic helper  (useful for troubleshooting inside Dynamo)
# ---------------------------------------------------------------------------

def diagnostics() -> Dict[str, Any]:
    """
    Return a diagnostic dictionary with environment and session information.

    Wire the output to a Watch node in Dynamo to inspect the Civil 3D
    connection state without throwing exceptions.
    """
    info: Dict[str, Any] = {
        "python_version":  sys.version,
        "clr_available":   _CLR_AVAILABLE,
        "civil3d_loaded":  False,
        "civil3d_year":    None,
        "errors":          [],
    }

    if not _CLR_AVAILABLE:
        info["errors"].append("clr not available")
        return info

    session = Civil3DSession()
    info["civil3d_loaded"] = session.is_loaded
    info["civil3d_year"]   = session.version_year
    info["errors"]         = session.errors

    if session.is_loaded:
        try:
            doc = session.active_document
            info["document_name"] = doc.Name if hasattr(doc, "Name") else str(doc)
        except Exception as exc:
            info["errors"].append(f"Could not read active document: {exc}")

    return info
