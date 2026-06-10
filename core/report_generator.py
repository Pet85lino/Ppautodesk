"""
core/report_generator.py
------------------------
Generador de reportes tecnicos de diagnostico.

Formato principal: PDF multipagina (matplotlib backend Agg/PdfPages):
    pagina 1 -> identificacion, bateria, latencia, estabilidad y
                diagnostico final en texto.
    pagina 2 -> graficas: historial de bateria y de RSSI.

Si matplotlib no esta disponible se genera un reporte HTML equivalente
(degradacion elegante: el diagnostico nunca se pierde).
"""

from __future__ import annotations

import html
import logging
from datetime import datetime
from pathlib import Path

logger = logging.getLogger("lino.core.report")

ACCENT = "#00E5FF"
BG = "#0A1428"
TEXT = "#E6F1FF"
MUTED = "#7A8BA3"


def _verdict(report: dict) -> str:
    """Diagnostico final combinando los indicadores disponibles."""
    problems: list[str] = []

    health = (report.get("analytics") or {}).get("battery_health")
    if health and health["health_score"] < 0.8:
        problems.append(
            f"degradacion de bateria estimada {health['estimated_degradation_pct']:.0f}%"
        )

    stability = (report.get("analytics") or {}).get("stability")
    if stability and stability["score"] < 60:
        problems.append(f"enlace inestable ({stability['score']:.0f}/100)")

    latency = report.get("latency")
    if latency and latency.get("classification") in ("Elevada", "Alta"):
        problems.append(f"latencia {latency['classification'].lower()}")

    rms = report.get("rms")
    if rms and abs(rms.get("diff_db", 0.0)) > 3.0:
        problems.append(f"desbalance L/R de {abs(rms['diff_db']):.1f} dB")

    if not problems:
        return "APTO: sin anomalias detectadas en los indicadores medidos."
    return "REVISAR: " + "; ".join(problems) + "."


def _report_lines(report: dict) -> list[tuple[str, str]]:
    """Pares (etiqueta, valor) que componen el cuerpo del reporte."""
    device = report.get("device", {})
    analytics = report.get("analytics") or {}
    lines: list[tuple[str, str]] = [
        ("Dispositivo", device.get("name", "desconocido")),
        ("MAC", device.get("mac", "--")),
        ("Fabricante", device.get("manufacturer") or "Desconocido"),
        ("RSSI en escaneo", f"{device.get('rssi', '--')} dBm"),
    ]

    caps = analytics.get("capabilities")
    if caps:
        lines.append(("MTU", str(caps.get("mtu") or "--")))
        codecs = caps.get("codecs") or []
        if codecs:
            lines.append(("Codecs probables", ", ".join(codecs)))
        if caps.get("avg_rssi") is not None:
            lines.append(("RSSI promedio sesion", f"{caps['avg_rssi']:.1f} dBm"))

    battery = report.get("battery")
    lines.append(
        ("Bateria", f"{battery}%" if battery is not None else "no expuesta (protocolo propietario)")
    )

    health = analytics.get("battery_health")
    if health:
        lines.append(
            ("Salud de bateria",
             f"{health['health_score'] * 100:.0f}% "
             f"(degradacion estimada {health['estimated_degradation_pct']:.0f}%)")
        )

    latency = report.get("latency")
    if latency:
        lines.append(
            ("Latencia",
             f"{latency['latency_ms']:.1f} ms ({latency['classification']}, "
             f"confianza {latency.get('confidence', 0):.2f})")
        )
    jitter = report.get("jitter")
    if jitter:
        lines.append(
            ("Jitter", f"media {jitter['mean_ms']:.1f} ms, sigma {jitter['std_ms']:.1f} ms")
        )

    rms = report.get("rms")
    if rms:
        lines.append(("Balance RMS L/R", f"{rms['diff_db']:+.1f} dB"))

    stability = analytics.get("stability")
    if stability:
        lines.append(("Estabilidad de enlace", f"{stability['score']:.0f}/100"))

    rssi = analytics.get("rssi")
    if rssi:
        lines.append(
            ("RSSI historico", f"media {rssi['mean_dbm']} dBm, varianza {rssi['variance']}")
        )
    return lines


def generate_report(report: dict, db, out_dir: Path) -> Path | None:
    """Genera el reporte tecnico (PDF si hay matplotlib, HTML si no).

    Args:
        report: datos del pipeline de diagnostico (core.diagnostics).
        db: DatabaseManager para las series historicas de las graficas.
        out_dir: carpeta destino (se crea si no existe).
    """
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = f"{datetime.now():%Y%m%d_%H%M%S}"
    mac = report.get("device", {}).get("mac", "device")
    safe_mac = mac.replace(":", "")

    try:
        return _generate_pdf(report, db, out_dir / f"reporte_{safe_mac}_{stamp}.pdf")
    except ImportError:
        logger.warning("matplotlib no disponible: generando reporte HTML")
        return _generate_html(report, out_dir / f"reporte_{safe_mac}_{stamp}.html")
    except Exception as exc:  # noqa: BLE001 - el reporte no debe romper la app
        logger.error("Error generando PDF (%s); intentando HTML", exc)
        return _generate_html(report, out_dir / f"reporte_{safe_mac}_{stamp}.html")


def _generate_pdf(report: dict, db, out_path: Path) -> Path:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.backends.backend_pdf import PdfPages

    mac = report.get("device", {}).get("mac", "")

    with PdfPages(out_path) as pdf:
        # ---------- Pagina 1: resumen tecnico ----------
        fig = plt.figure(figsize=(8.27, 11.69), facecolor=BG)  # A4
        fig.text(0.07, 0.95, "LINO AUDIO DIAGNOSTIC", color=ACCENT,
                 fontsize=18, fontweight="bold")
        fig.text(0.07, 0.925, "Reporte tecnico de auriculares TWS",
                 color=MUTED, fontsize=11)
        fig.text(0.07, 0.905, f"Generado: {datetime.now():%Y-%m-%d %H:%M:%S}",
                 color=MUTED, fontsize=9)

        y = 0.85
        for label, value in _report_lines(report):
            fig.text(0.07, y, f"{label}:", color=MUTED, fontsize=10)
            fig.text(0.35, y, str(value), color=TEXT, fontsize=10)
            y -= 0.028

        fig.text(0.07, y - 0.02, "DIAGNOSTICO FINAL", color=ACCENT,
                 fontsize=12, fontweight="bold")
        fig.text(0.07, y - 0.05, _verdict(report), color=TEXT, fontsize=10, wrap=True)
        pdf.savefig(fig)
        plt.close(fig)

        # ---------- Pagina 2: graficas historicas ----------
        battery_rows = db.battery_history(mac, limit=100) if mac else []
        rssi_rows = []
        if mac:
            try:
                rssi_rows = db._conn.execute(
                    "SELECT timestamp, rssi FROM scan_history WHERE mac = ? "
                    "ORDER BY id DESC LIMIT 100",
                    (mac,),
                ).fetchall()[::-1]
            except Exception:  # noqa: BLE001
                rssi_rows = []

        if battery_rows or rssi_rows:
            fig, axes = plt.subplots(2, 1, figsize=(8.27, 11.69), facecolor=BG)
            for ax in axes:
                ax.set_facecolor("#081020")
                ax.tick_params(colors=MUTED, labelsize=7)
                for spine in ax.spines.values():
                    spine.set_color("#1E2A44")

            if battery_rows:
                axes[0].plot([r[1] for r in battery_rows], color="#2EE6A8", marker="o",
                             markersize=3, linewidth=1)
            axes[0].set_title("Historial de bateria (%)", color=TEXT, fontsize=11)

            if rssi_rows:
                axes[1].plot([r[1] for r in rssi_rows], color=ACCENT, linewidth=1)
            axes[1].set_title("Historial RSSI (dBm)", color=TEXT, fontsize=11)

            fig.tight_layout()
            pdf.savefig(fig)
            plt.close(fig)

    logger.info("Reporte PDF generado: %s", out_path)
    return out_path


def _generate_html(report: dict, out_path: Path) -> Path | None:
    """Reporte HTML de respaldo (sin dependencias externas)."""
    try:
        rows = "".join(
            f"<tr><td>{html.escape(label)}</td><td>{html.escape(str(value))}</td></tr>"
            for label, value in _report_lines(report)
        )
        out_path.write_text(
            f"""<!doctype html><html><head><meta charset="utf-8">
<title>LINO Audio Diagnostic - Reporte</title>
<style>
body {{ background:{BG}; color:{TEXT}; font-family:Segoe UI,sans-serif; padding:2em; }}
h1 {{ color:{ACCENT}; }} td {{ padding:4px 14px; border-bottom:1px solid #1E2A44; }}
.verdict {{ margin-top:1.5em; padding:1em; border:1px solid {ACCENT}; border-radius:8px; }}
</style></head><body>
<h1>LINO AUDIO DIAGNOSTIC</h1>
<p>Reporte tecnico - {datetime.now():%Y-%m-%d %H:%M:%S}</p>
<table>{rows}</table>
<div class="verdict"><b>DIAGNOSTICO FINAL:</b> {html.escape(_verdict(report))}</div>
</body></html>""",
            encoding="utf-8",
        )
        logger.info("Reporte HTML generado: %s", out_path)
        return out_path
    except OSError as exc:
        logger.error("Error generando reporte HTML: %s", exc)
        return None
