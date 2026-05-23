// ============================================================================
// ReportGenerator.cs
// Civil 3D DWG Analysis – Multi-Format Report Output
// Compatible with Autodesk Civil 3D 2025 / 2026 / 2027
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Civil3DConnector.Models;

namespace Civil3DConnector.Analyzers
{
    /// <summary>
    /// Controls what content is included in generated reports.
    /// </summary>
    public sealed class ReportOptions
    {
        /// <summary>
        /// Include object inventory section (lists every Civil 3D object scanned).
        /// Default: <c>true</c>.
        /// </summary>
        public bool IncludeObjectInventory { get; set; } = true;

        /// <summary>
        /// Include fix suggestions for each issue. Default: <c>true</c>.
        /// </summary>
        public bool IncludeFixSuggestions { get; set; } = true;

        /// <summary>
        /// Include Info-level items in reports. Default: <c>true</c>.
        /// </summary>
        public bool IncludeInfoItems { get; set; } = true;

        /// <summary>
        /// Name of the organisation / project shown in report headers.
        /// </summary>
        public string OrganizationName { get; set; } = "Civil 3D Analysis";

        /// <summary>
        /// Project number or identifier embedded in reports.
        /// </summary>
        public string ProjectNumber { get; set; } = string.Empty;

        /// <summary>
        /// Company logo URI embedded in the HTML report (data URI or HTTP URL).
        /// Leave empty to omit the logo.
        /// </summary>
        public string LogoUri { get; set; } = string.Empty;

        /// <summary>Default options.</summary>
        public static ReportOptions Default => new ReportOptions();
    }

    /// <summary>
    /// Generates analysis reports from an <see cref="AnalysisResult"/> in four
    /// formats: HTML (with CSS styling and collapsible sections), JSON, XML
    /// (LandXML-compatible wrapper), and CSV (suitable for Excel).
    ///
    /// <para>
    /// All public methods accept a <see cref="ReportOptions"/> instance; pass
    /// <c>null</c> to use <see cref="ReportOptions.Default"/>.
    /// </para>
    ///
    /// <example>
    /// <code>
    /// var generator = new ReportGenerator();
    /// string html = generator.GenerateHtml(result, ReportOptions.Default);
    /// File.WriteAllText("report.html", html, Encoding.UTF8);
    ///
    /// string json = generator.GenerateJson(result);
    /// File.WriteAllText("report.json", json, Encoding.UTF8);
    /// </code>
    /// </example>
    /// </summary>
    public sealed class ReportGenerator
    {
        // ------------------------------------------------------------------
        // Severity colour / badge helpers (shared across formats)
        // ------------------------------------------------------------------

        private static string SeverityColor(IssueSeverity s) => s switch
        {
            IssueSeverity.Critical => "#c0392b",
            IssueSeverity.Warning  => "#d68910",
            IssueSeverity.Info     => "#1a6496",
            _                      => "#555555"
        };

        private static string SeverityBg(IssueSeverity s) => s switch
        {
            IssueSeverity.Critical => "#fdecea",
            IssueSeverity.Warning  => "#fef9e7",
            IssueSeverity.Info     => "#eaf4fb",
            _                      => "#f5f5f5"
        };

        private static string SeverityLabel(IssueSeverity s) => s switch
        {
            IssueSeverity.Critical => "CRITICAL",
            IssueSeverity.Warning  => "WARNING",
            IssueSeverity.Info     => "INFO",
            _                      => s.ToString().ToUpperInvariant()
        };

        // ==================================================================
        // HTML Report
        // ==================================================================

        /// <summary>
        /// Generates a standalone, self-contained HTML report with inline CSS,
        /// collapsible sections, severity colour coding, summary statistics, and
        /// fix recommendations.
        /// </summary>
        /// <param name="result">Populated analysis result.</param>
        /// <param name="options">Report options; <c>null</c> = defaults.</param>
        /// <returns>UTF-8 HTML string.</returns>
        public string GenerateHtml(AnalysisResult result, ReportOptions? options = null)
        {
            if (result is null) throw new ArgumentNullException(nameof(result));
            options ??= ReportOptions.Default;

            // Ensure summary is computed.
            var summary = result.Summary ?? result.ComputeSummary();

            var sb = new StringBuilder(64_000);

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"UTF-8\">");
            sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendLine($"  <title>Civil 3D DWG Analysis – {HtmlEncode(Path.GetFileName(result.DwgFilePath))}</title>");
            sb.AppendLine(BuildCss());
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            // ---- Header ----
            sb.AppendLine("<header class=\"report-header\">");
            if (!string.IsNullOrEmpty(options.LogoUri))
                sb.AppendLine($"  <img class=\"logo\" src=\"{options.LogoUri}\" alt=\"Logo\">");
            sb.AppendLine($"  <h1>{HtmlEncode(options.OrganizationName)}</h1>");
            sb.AppendLine("  <h2>Civil 3D DWG Analysis Report</h2>");
            if (!string.IsNullOrEmpty(options.ProjectNumber))
                sb.AppendLine($"  <p class=\"project-num\">Project: {HtmlEncode(options.ProjectNumber)}</p>");
            sb.AppendLine("</header>");

            // ---- Meta info table ----
            sb.AppendLine("<section class=\"meta-section\">");
            sb.AppendLine("  <table class=\"meta-table\">");
            AppendMetaRow(sb, "DWG File",        HtmlEncode(result.DwgFilePath));
            AppendMetaRow(sb, "File Size",        FormatFileSize(result.FileSizeBytes));
            AppendMetaRow(sb, "Drawing Version",  HtmlEncode(result.DrawingVersion));
            AppendMetaRow(sb, "Civil 3D Version", HtmlEncode(result.Civil3DVersion));
            AppendMetaRow(sb, "Coordinate System",HtmlEncode(result.CoordinateSystem));
            AppendMetaRow(sb, "Analysis Started", result.StartedAt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            AppendMetaRow(sb, "Duration",         result.Duration.TotalSeconds.ToString("F1") + " s");
            AppendMetaRow(sb, "Analyzer Version", HtmlEncode(result.AnalyzerVersion));
            sb.AppendLine("  </table>");
            sb.AppendLine("</section>");

            // ---- Health score ----
            AppendHealthScoreHtml(sb, summary);

            // ---- Summary statistics ----
            AppendSummaryHtml(sb, summary);

            // ---- Issues by severity ----
            foreach (IssueSeverity severity in new[] {
                IssueSeverity.Critical,
                IssueSeverity.Warning,
                IssueSeverity.Info })
            {
                if (severity == IssueSeverity.Info && !options.IncludeInfoItems)
                    continue;

                var severityIssues = result.Issues
                    .Where(i => i.Severity == severity)
                    .OrderBy(i => i.Category)
                    .ThenBy(i => i.ObjectType)
                    .ThenBy(i => i.ObjectName)
                    .ToList();

                if (severityIssues.Count == 0) continue;

                string sectionId = $"section-{severity.ToString().ToLower()}";
                string color     = SeverityColor(severity);
                string bg        = SeverityBg(severity);

                sb.AppendLine($"<section class=\"issues-section\" style=\"border-left:4px solid {color};\">");
                sb.AppendLine($"  <button class=\"section-toggle\" onclick=\"toggleSection('{sectionId}')\" " +
                              $"style=\"background:{bg};color:{color};\">");
                sb.AppendLine($"    <span class=\"badge\" style=\"background:{color};\">" +
                              $"{severityIssues.Count}</span> " +
                              $"{SeverityLabel(severity)} Issues");
                sb.AppendLine("    <span class=\"chevron\">▼</span>");
                sb.AppendLine("  </button>");
                sb.AppendLine($"  <div id=\"{sectionId}\" class=\"section-content\">");

                // Group by category within severity.
                foreach (var catGroup in severityIssues.GroupBy(i => i.Category).OrderBy(g => g.Key))
                {
                    string catId = $"cat-{severity}-{catGroup.Key}";
                    sb.AppendLine($"    <div class=\"category-group\">");
                    sb.AppendLine($"      <button class=\"cat-toggle\" onclick=\"toggleSection('{catId}')\">");
                    sb.AppendLine($"        {HtmlEncode(catGroup.Key.ToString())} " +
                                  $"<span class=\"count-badge\">{catGroup.Count()}</span>");
                    sb.AppendLine("        <span class=\"chevron\">▼</span>");
                    sb.AppendLine("      </button>");
                    sb.AppendLine($"      <div id=\"{catId}\" class=\"cat-content\">");

                    foreach (var issue in catGroup)
                    {
                        AppendIssueCardHtml(sb, issue, options, color);
                    }

                    sb.AppendLine("      </div>"); // cat-content
                    sb.AppendLine("    </div>");   // category-group
                }

                sb.AppendLine("  </div>"); // section-content
                sb.AppendLine("</section>");
            }

            // ---- Object inventory ----
            if (options.IncludeObjectInventory && result.ObjectInventory.Count > 0)
            {
                AppendInventoryHtml(sb, result);
            }

            // ---- Footer ----
            sb.AppendLine("<footer class=\"report-footer\">");
            sb.AppendLine($"  <p>Generated by Civil3DConnector v{HtmlEncode(result.AnalyzerVersion)} " +
                          $"on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC. Run ID: {result.RunId:N}</p>");
            sb.AppendLine("</footer>");

            // ---- JavaScript ----
            sb.AppendLine(BuildJavaScript());

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            return sb.ToString();
        }

        // ---- HTML helpers ----

        private static void AppendMetaRow(StringBuilder sb, string label, string value)
        {
            sb.AppendLine($"    <tr><th>{HtmlEncode(label)}</th><td>{value}</td></tr>");
        }

        private static void AppendHealthScoreHtml(StringBuilder sb, AnalysisSummary summary)
        {
            string tier  = summary.HealthTier;
            int    score = summary.HealthScore;
            string color = score >= 90 ? "#27ae60" :
                           score >= 70 ? "#2980b9" :
                           score >= 50 ? "#d68910" :
                           score >= 25 ? "#e67e22" : "#c0392b";

            sb.AppendLine("<section class=\"health-section\">");
            sb.AppendLine("  <h3>Overall Health Score</h3>");
            sb.AppendLine("  <div class=\"health-gauge\">");
            sb.AppendLine($"    <div class=\"health-score\" style=\"color:{color};\">{score}</div>");
            sb.AppendLine($"    <div class=\"health-tier\" style=\"color:{color};\">{HtmlEncode(tier)}</div>");
            sb.AppendLine("    <div class=\"progress-bar-bg\">");
            sb.AppendLine($"      <div class=\"progress-bar-fill\" style=\"width:{score}%;background:{color};\"></div>");
            sb.AppendLine("    </div>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</section>");
        }

        private static void AppendSummaryHtml(StringBuilder sb, AnalysisSummary summary)
        {
            sb.AppendLine("<section class=\"summary-section\">");
            sb.AppendLine("  <h3>Summary Statistics</h3>");
            sb.AppendLine("  <div class=\"stat-cards\">");
            AppendStatCard(sb, "Total Issues",     summary.TotalIssues.ToString(),     "#555");
            AppendStatCard(sb, "Critical",         summary.CriticalCount.ToString(),   SeverityColor(IssueSeverity.Critical));
            AppendStatCard(sb, "Warnings",         summary.WarningCount.ToString(),    SeverityColor(IssueSeverity.Warning));
            AppendStatCard(sb, "Info",             summary.InfoCount.ToString(),       SeverityColor(IssueSeverity.Info));
            AppendStatCard(sb, "Affected Objects", summary.AffectedObjectCount.ToString(), "#8e44ad");
            sb.AppendLine("  </div>");

            // By category table.
            if (summary.ByCategory.Count > 0)
            {
                sb.AppendLine("  <h4>Issues by Category</h4>");
                sb.AppendLine("  <table class=\"summary-table\">");
                sb.AppendLine("    <thead><tr><th>Category</th><th>Count</th></tr></thead>");
                sb.AppendLine("    <tbody>");
                foreach (var kv in summary.ByCategory.OrderByDescending(kv => kv.Value))
                {
                    sb.AppendLine($"      <tr><td>{HtmlEncode(kv.Key.ToString())}</td>" +
                                  $"<td class=\"num-cell\">{kv.Value}</td></tr>");
                }
                sb.AppendLine("    </tbody></table>");
            }

            // By object type table.
            if (summary.ByObjectType.Count > 0)
            {
                sb.AppendLine("  <h4>Issues by Object Type</h4>");
                sb.AppendLine("  <table class=\"summary-table\">");
                sb.AppendLine("    <thead><tr><th>Object Type</th><th>Count</th></tr></thead>");
                sb.AppendLine("    <tbody>");
                foreach (var kv in summary.ByObjectType.OrderByDescending(kv => kv.Value))
                {
                    sb.AppendLine($"      <tr><td>{HtmlEncode(kv.Key.ToString())}</td>" +
                                  $"<td class=\"num-cell\">{kv.Value}</td></tr>");
                }
                sb.AppendLine("    </tbody></table>");
            }

            sb.AppendLine("</section>");
        }

        private static void AppendStatCard(StringBuilder sb, string label, string value, string color)
        {
            sb.AppendLine($"    <div class=\"stat-card\" style=\"border-top:3px solid {color};\">");
            sb.AppendLine($"      <div class=\"stat-value\" style=\"color:{color};\">{value}</div>");
            sb.AppendLine($"      <div class=\"stat-label\">{HtmlEncode(label)}</div>");
            sb.AppendLine("    </div>");
        }

        private static void AppendIssueCardHtml(
            StringBuilder sb,
            ObjectIssue   issue,
            ReportOptions options,
            string        severityColor)
        {
            string badgeBg  = SeverityBg(issue.Severity);
            string stationStr = issue.Station.HasValue
                ? $"Station {issue.Station.Value:F3}" : string.Empty;

            sb.AppendLine("        <div class=\"issue-card\">");
            sb.AppendLine($"          <div class=\"issue-header\" style=\"background:{badgeBg};\">");
            sb.AppendLine($"            <span class=\"severity-badge\" style=\"background:{severityColor};\">" +
                          $"{SeverityLabel(issue.Severity)}</span>");
            sb.AppendLine($"            <span class=\"object-type\">{HtmlEncode(issue.ObjectType.ToString())}</span>");
            sb.AppendLine($"            <span class=\"object-name\">{HtmlEncode(issue.ObjectName)}</span>");
            if (!string.IsNullOrEmpty(stationStr))
                sb.AppendLine($"            <span class=\"station\">{HtmlEncode(stationStr)}</span>");
            sb.AppendLine($"            <span class=\"issue-category\">{HtmlEncode(issue.Category.ToString())}</span>");
            sb.AppendLine("          </div>");

            sb.AppendLine("          <div class=\"issue-body\">");
            sb.AppendLine($"            <p class=\"issue-message\">{HtmlEncode(issue.Message)}</p>");

            if (!string.IsNullOrEmpty(issue.Detail))
                sb.AppendLine($"            <p class=\"issue-detail\">{HtmlEncode(issue.Detail)}</p>");

            if (!string.IsNullOrEmpty(issue.ObjectHandle))
                sb.AppendLine($"            <p class=\"meta-item\">Handle: <code>{HtmlEncode(issue.ObjectHandle)}</code></p>");

            if (!string.IsNullOrEmpty(issue.LayerName))
                sb.AppendLine($"            <p class=\"meta-item\">Layer: {HtmlEncode(issue.LayerName)}</p>");

            // Metadata key-value pairs.
            foreach (var kv in issue.Metadata)
            {
                sb.AppendLine($"            <p class=\"meta-item\">{HtmlEncode(kv.Key)}: {HtmlEncode(kv.Value)}</p>");
            }

            // Fix suggestions.
            if (options.IncludeFixSuggestions && issue.FixSuggestions.Count > 0)
            {
                sb.AppendLine("            <div class=\"fix-suggestions\">");
                sb.AppendLine("              <strong>Recommended Fix(es):</strong>");
                sb.AppendLine("              <ol>");
                foreach (var fix in issue.FixSuggestions)
                {
                    sb.AppendLine("                <li class=\"fix-item\">");
                    sb.AppendLine($"                  <strong>{HtmlEncode(fix.Title)}</strong>");
                    if (!string.IsNullOrEmpty(fix.Description))
                        sb.AppendLine($"                  <p>{HtmlEncode(fix.Description)}</p>");
                    string autoStr = fix.IsAutomatable ? "Yes" : "No";
                    sb.AppendLine($"                  <span class=\"fix-meta\">Automatable: {autoStr} | " +
                                  $"Est. manual effort: {fix.EstimatedManualMinutes} min</span>");
                    sb.AppendLine("                </li>");
                }
                sb.AppendLine("              </ol>");
                sb.AppendLine("            </div>");
            }

            sb.AppendLine("          </div>"); // issue-body
            sb.AppendLine("        </div>");   // issue-card
        }

        private static void AppendInventoryHtml(StringBuilder sb, AnalysisResult result)
        {
            sb.AppendLine("<section class=\"inventory-section\">");
            sb.AppendLine("  <button class=\"section-toggle\" onclick=\"toggleSection('inventory-body')\" " +
                          "style=\"background:#f0f0f0;\">");
            sb.AppendLine($"    Object Inventory ({result.ObjectInventory.Count} objects)");
            sb.AppendLine("    <span class=\"chevron\">▼</span>");
            sb.AppendLine("  </button>");
            sb.AppendLine("  <div id=\"inventory-body\" class=\"section-content\" style=\"display:none;\">");
            sb.AppendLine("    <table class=\"inventory-table\">");
            sb.AppendLine("      <thead><tr><th>Handle</th><th>Name</th><th>Type</th>" +
                          "<th>Layer</th><th>Issues</th><th>Max Severity</th></tr></thead>");
            sb.AppendLine("      <tbody>");

            foreach (var rec in result.ObjectInventory.OrderByDescending(r => r.MaxSeverity).ThenBy(r => r.Type))
            {
                string maxSev = rec.MaxSeverity.HasValue
                    ? $"<span style=\"color:{SeverityColor(rec.MaxSeverity.Value)};font-weight:bold;\">" +
                      $"{SeverityLabel(rec.MaxSeverity.Value)}</span>"
                    : "<span style=\"color:#27ae60;\">Clean</span>";

                sb.AppendLine($"        <tr>" +
                              $"<td><code>{HtmlEncode(rec.Handle)}</code></td>" +
                              $"<td>{HtmlEncode(rec.Name)}</td>" +
                              $"<td>{HtmlEncode(rec.Type.ToString())}</td>" +
                              $"<td>{HtmlEncode(rec.Layer)}</td>" +
                              $"<td class=\"num-cell\">{rec.IssueCount}</td>" +
                              $"<td>{maxSev}</td>" +
                              $"</tr>");
            }

            sb.AppendLine("      </tbody>");
            sb.AppendLine("    </table>");
            sb.AppendLine("  </div>");
            sb.AppendLine("</section>");
        }

        // ---- Embedded CSS ----

        private static string BuildCss() => @"
<style>
/* ---- Reset & base ---- */
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',Arial,sans-serif;font-size:14px;color:#222;background:#f7f8fa;line-height:1.5}
h1{font-size:1.6rem;font-weight:700}h2{font-size:1.2rem;font-weight:600;margin-top:.25rem}
h3{font-size:1.05rem;font-weight:600;margin:1rem 0 .5rem}
h4{font-size:.95rem;font-weight:600;margin:.75rem 0 .35rem}
code{background:#eee;border-radius:3px;padding:1px 4px;font-size:.85em}
/* ---- Layout ---- */
.report-header{background:linear-gradient(135deg,#1a2a4a,#2c4880);color:#fff;padding:1.5rem 2rem;display:flex;flex-direction:column;gap:.3rem}
.report-header .logo{max-height:60px;margin-bottom:.5rem}
.report-header .project-num{font-size:.85rem;opacity:.8}
section{margin:1rem 1.5rem;background:#fff;border-radius:6px;box-shadow:0 1px 4px rgba(0,0,0,.08);overflow:hidden}
/* ---- Meta table ---- */
.meta-section{padding:1rem}
.meta-table{width:100%;border-collapse:collapse}
.meta-table th{text-align:left;padding:.35rem .75rem;background:#f0f2f5;font-weight:600;width:160px;border-bottom:1px solid #e0e0e0}
.meta-table td{padding:.35rem .75rem;border-bottom:1px solid #e8e8e8}
/* ---- Health ---- */
.health-section{padding:1rem 1.5rem}
.health-gauge{display:flex;align-items:center;gap:1.5rem;flex-wrap:wrap}
.health-score{font-size:3rem;font-weight:800;line-height:1}
.health-tier{font-size:1.1rem;font-weight:600}
.progress-bar-bg{flex:1;min-width:120px;height:16px;background:#e0e0e0;border-radius:8px;overflow:hidden}
.progress-bar-fill{height:100%;border-radius:8px;transition:width .5s}
/* ---- Summary ---- */
.summary-section{padding:1rem 1.5rem}
.stat-cards{display:flex;flex-wrap:wrap;gap:.75rem;margin-bottom:1rem}
.stat-card{background:#fff;border:1px solid #e8e8e8;border-radius:6px;padding:.75rem 1.25rem;min-width:110px;text-align:center;box-shadow:0 1px 3px rgba(0,0,0,.06)}
.stat-value{font-size:1.8rem;font-weight:800;line-height:1.1}
.stat-label{font-size:.78rem;color:#666;margin-top:.2rem;text-transform:uppercase;letter-spacing:.04em}
.summary-table{width:100%;border-collapse:collapse;margin-top:.5rem}
.summary-table th{background:#f0f2f5;padding:.3rem .6rem;text-align:left;font-size:.85rem;font-weight:600}
.summary-table td{padding:.28rem .6rem;border-bottom:1px solid #f0f0f0;font-size:.85rem}
.num-cell{text-align:right;font-variant-numeric:tabular-nums}
/* ---- Section toggles ---- */
.issues-section,.inventory-section{padding:0}
.section-toggle,.cat-toggle{width:100%;text-align:left;border:none;cursor:pointer;padding:.75rem 1.25rem;font-size:.95rem;font-weight:600;display:flex;align-items:center;gap:.5rem}
.section-toggle{font-size:1rem}
.cat-toggle{background:#f9f9f9;font-size:.88rem;font-weight:500;padding:.5rem 1.5rem;border-bottom:1px solid #eee}
.chevron{margin-left:auto;font-size:.75rem;transition:transform .2s}
.badge{color:#fff;border-radius:12px;padding:2px 8px;font-size:.78rem;margin-right:.25rem}
.count-badge{background:#999;color:#fff;border-radius:10px;padding:1px 7px;font-size:.75rem;margin-left:.3rem}
.section-content,.cat-content{padding:0}
/* ---- Issue cards ---- */
.issue-card{border-bottom:1px solid #f0f0f0;padding:.5rem 1.5rem 1rem}
.issue-header{display:flex;flex-wrap:wrap;align-items:center;gap:.5rem;padding:.4rem .6rem;border-radius:4px;margin-bottom:.5rem}
.severity-badge{color:#fff;border-radius:3px;padding:2px 8px;font-size:.72rem;font-weight:700;text-transform:uppercase;letter-spacing:.04em}
.object-type{background:#e8eaf6;color:#3949ab;border-radius:3px;padding:2px 6px;font-size:.75rem}
.object-name{font-weight:600;font-size:.88rem}
.station{color:#555;font-size:.8rem;font-style:italic}
.issue-category{margin-left:auto;color:#777;font-size:.75rem}
.issue-body{padding:0 .6rem}
.issue-message{font-size:.9rem;font-weight:500;color:#222}
.issue-detail{font-size:.82rem;color:#555;margin-top:.25rem}
.meta-item{font-size:.78rem;color:#666;margin-top:.2rem}
.fix-suggestions{margin-top:.6rem;background:#f9fff9;border:1px solid #c8e6c9;border-radius:4px;padding:.6rem .9rem;font-size:.83rem}
.fix-item{margin:.3rem 0 .3rem 1rem}
.fix-item p{color:#555;margin:.15rem 0}
.fix-meta{font-size:.75rem;color:#777}
/* ---- Inventory ---- */
.inventory-table{width:100%;border-collapse:collapse;font-size:.83rem}
.inventory-table th{background:#f0f2f5;padding:.3rem .5rem;font-weight:600;border-bottom:1px solid #ddd}
.inventory-table td{padding:.25rem .5rem;border-bottom:1px solid #f0f0f0}
/* ---- Footer ---- */
.report-footer{margin:1rem 1.5rem;text-align:center;color:#888;font-size:.78rem;padding:.5rem}
/* ---- Print ---- */
@media print{
  .section-toggle,.cat-toggle{pointer-events:none}
  .section-content,.cat-content{display:block!important}
  section{box-shadow:none;border:1px solid #ccc}
  body{background:#fff}
}
</style>";

        // ---- Embedded JavaScript ----

        private static string BuildJavaScript() => @"
<script>
function toggleSection(id) {
  var el = document.getElementById(id);
  if (!el) return;
  el.style.display = el.style.display === 'none' ? '' : 'none';
}
// Collapse all Info sections by default on load.
document.addEventListener('DOMContentLoaded', function() {
  var infoSection = document.getElementById('section-info');
  if (infoSection) infoSection.style.display = 'none';
  var inv = document.getElementById('inventory-body');
  if (inv) inv.style.display = 'none';
});
</script>";

        // ==================================================================
        // JSON Report
        // ==================================================================

        /// <summary>
        /// Generates a structured JSON report suitable for API consumption or
        /// programmatic processing.
        /// </summary>
        /// <param name="result">Populated analysis result.</param>
        /// <param name="options">Report options.</param>
        /// <param name="indented">Whether to pretty-print the JSON. Default: <c>true</c>.</param>
        /// <returns>JSON string.</returns>
        public string GenerateJson(
            AnalysisResult result,
            ReportOptions? options  = null,
            bool           indented = true)
        {
            if (result is null) throw new ArgumentNullException(nameof(result));
            options ??= ReportOptions.Default;

            var summary = result.Summary ?? result.ComputeSummary();

            // Build anonymous DTO to control serialization shape.
            var dto = new
            {
                runId           = result.RunId,
                dwgFilePath     = result.DwgFilePath,
                fileSizeBytes   = result.FileSizeBytes,
                drawingVersion  = result.DrawingVersion,
                civil3DVersion  = result.Civil3DVersion,
                coordinateSystem= result.CoordinateSystem,
                analyzerVersion = result.AnalyzerVersion,
                startedAt       = result.StartedAt,
                completedAt     = result.CompletedAt,
                durationSeconds = result.Duration.TotalSeconds,
                isSuccessful    = result.IsSuccessful,
                fatalError      = result.FatalError,
                summary = new
                {
                    totalIssues         = summary.TotalIssues,
                    criticalCount       = summary.CriticalCount,
                    warningCount        = summary.WarningCount,
                    infoCount           = summary.InfoCount,
                    affectedObjectCount = summary.AffectedObjectCount,
                    healthScore         = summary.HealthScore,
                    healthTier          = summary.HealthTier,
                    byCategory          = summary.ByCategory.ToDictionary(
                        kv => kv.Key.ToString(),
                        kv => kv.Value),
                    byObjectType        = summary.ByObjectType.ToDictionary(
                        kv => kv.Key.ToString(),
                        kv => kv.Value)
                },
                issues = result.Issues
                    .Where(i => options.IncludeInfoItems || i.Severity != IssueSeverity.Info)
                    .Select(i => new
                    {
                        issueId      = i.IssueId,
                        objectHandle = i.ObjectHandle,
                        objectName   = i.ObjectName,
                        objectType   = i.ObjectType.ToString(),
                        rxClassName  = i.RxClassName,
                        severity     = i.Severity.ToString(),
                        category     = i.Category.ToString(),
                        message      = i.Message,
                        detail       = i.Detail,
                        elementIndex = i.ElementIndex,
                        station      = i.Station,
                        layerName    = i.LayerName,
                        detectedAt   = i.DetectedAt,
                        metadata     = i.Metadata,
                        fixSuggestions = !options.IncludeFixSuggestions
                            ? Array.Empty<object>()
                            : (object)i.FixSuggestions.Select(f => new
                            {
                                title                  = f.Title,
                                description            = f.Description,
                                isAutomatable          = f.IsAutomatable,
                                repairActionType       = f.RepairActionType,
                                estimatedManualMinutes = f.EstimatedManualMinutes
                            })
                    }),
                objectInventory = options.IncludeObjectInventory
                    ? (object)result.ObjectInventory.Select(r => new
                    {
                        handle      = r.Handle,
                        name        = r.Name,
                        type        = r.Type.ToString(),
                        layer       = r.Layer,
                        issueCount  = r.IssueCount,
                        maxSeverity = r.MaxSeverity?.ToString(),
                        properties  = r.Properties
                    })
                    : Array.Empty<object>()
            };

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented         = indented,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy  = JsonNamingPolicy.CamelCase
            };

            return JsonSerializer.Serialize(dto, jsonOptions);
        }

        // ==================================================================
        // XML Report (LandXML-compatible wrapper)
        // ==================================================================

        /// <summary>
        /// Generates an XML report wrapped in a LandXML-compatible envelope.
        /// The issues are placed in an <c>&lt;AnalysisReport&gt;</c> extension
        /// element so the file can coexist with LandXML data.
        /// </summary>
        /// <param name="result">Populated analysis result.</param>
        /// <param name="options">Report options.</param>
        /// <returns>Well-formed XML string (UTF-8).</returns>
        public string GenerateXml(AnalysisResult result, ReportOptions? options = null)
        {
            if (result is null) throw new ArgumentNullException(nameof(result));
            options ??= ReportOptions.Default;

            var summary = result.Summary ?? result.ComputeSummary();

            // LandXML root namespace.
            XNamespace lx  = "http://www.landxml.org/schema/LandXML-1.2";
            XNamespace ext = "http://schemas.civil3dconnector.com/analysis/1.0";

            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(lx + "LandXML",
                    new XAttribute("version", "1.2"),
                    new XAttribute("xmlns", lx.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "c3d", ext.NamespaceName),
                    new XAttribute("date", result.StartedAt.ToString("yyyy-MM-dd")),
                    new XAttribute("time", result.StartedAt.ToString("HH:mm:ss")),

                    // LandXML Units element (required by schema).
                    new XElement(lx + "Units",
                        new XElement(lx + "Metric",
                            new XAttribute("linearUnit", "meter"),
                            new XAttribute("areaUnit", "squareMeter"),
                            new XAttribute("volumeUnit", "cubicMeter"),
                            new XAttribute("angularUnit", "decimal dd.mm.ss"),
                            new XAttribute("directionUnit", "decimal dd.mm.ss"))),

                    // Project element.
                    new XElement(lx + "Project",
                        new XAttribute("name", options.ProjectNumber)),

                    // Analysis Report extension element.
                    new XElement(ext + "AnalysisReport",
                        new XAttribute("runId",          result.RunId.ToString("N")),
                        new XAttribute("analyzerVersion",result.AnalyzerVersion),
                        new XAttribute("isSuccessful",   result.IsSuccessful.ToString().ToLower()),

                        // Drawing metadata.
                        new XElement(ext + "DrawingMetadata",
                            new XAttribute("filePath",        result.DwgFilePath),
                            new XAttribute("fileSizeBytes",   result.FileSizeBytes),
                            new XAttribute("drawingVersion",  result.DrawingVersion),
                            new XAttribute("civil3DVersion",  result.Civil3DVersion),
                            new XAttribute("coordinateSystem",result.CoordinateSystem),
                            new XAttribute("startedAt",       result.StartedAt.ToString("o")),
                            new XAttribute("completedAt",     result.CompletedAt.ToString("o")),
                            new XAttribute("durationSec",     result.Duration.TotalSeconds.ToString("F2",
                                                              CultureInfo.InvariantCulture))),

                        // Summary element.
                        new XElement(ext + "Summary",
                            new XAttribute("totalIssues",         summary.TotalIssues),
                            new XAttribute("criticalCount",       summary.CriticalCount),
                            new XAttribute("warningCount",        summary.WarningCount),
                            new XAttribute("infoCount",           summary.InfoCount),
                            new XAttribute("affectedObjectCount", summary.AffectedObjectCount),
                            new XAttribute("healthScore",         summary.HealthScore),
                            new XAttribute("healthTier",          summary.HealthTier),

                            new XElement(ext + "ByCategory",
                                summary.ByCategory.Select(kv =>
                                    new XElement(ext + "Category",
                                        new XAttribute("name",  kv.Key.ToString()),
                                        new XAttribute("count", kv.Value)))),

                            new XElement(ext + "ByObjectType",
                                summary.ByObjectType.Select(kv =>
                                    new XElement(ext + "ObjectType",
                                        new XAttribute("name",  kv.Key.ToString()),
                                        new XAttribute("count", kv.Value))))),

                        // Issues.
                        new XElement(ext + "Issues",
                            result.Issues
                                .Where(i => options.IncludeInfoItems ||
                                            i.Severity != IssueSeverity.Info)
                                .Select(i => BuildIssueXml(i, options, ext))),

                        // Object inventory.
                        options.IncludeObjectInventory
                            ? new XElement(ext + "ObjectInventory",
                                result.ObjectInventory.Select(r =>
                                    new XElement(ext + "Object",
                                        new XAttribute("handle",      r.Handle),
                                        new XAttribute("name",        r.Name),
                                        new XAttribute("type",        r.Type.ToString()),
                                        new XAttribute("layer",       r.Layer),
                                        new XAttribute("issueCount",  r.IssueCount),
                                        r.MaxSeverity.HasValue
                                            ? new XAttribute("maxSeverity", r.MaxSeverity.Value.ToString())
                                            : null,
                                        r.Properties.Count > 0
                                            ? new XElement(ext + "Properties",
                                                r.Properties.Select(kv =>
                                                    new XElement(ext + "Property",
                                                        new XAttribute("key",   kv.Key),
                                                        new XAttribute("value", kv.Value))))
                                            : null)))
                            : null)));

            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, new XmlWriterSettings
            {
                Indent      = true,
                IndentChars = "  ",
                Encoding    = Encoding.UTF8
            }))
            {
                doc.Save(writer);
            }
            return sb.ToString();
        }

        private static XElement BuildIssueXml(ObjectIssue i, ReportOptions options, XNamespace ext)
        {
            var el = new XElement(ext + "Issue",
                new XAttribute("issueId",      i.IssueId.ToString("N")),
                new XAttribute("severity",     i.Severity.ToString()),
                new XAttribute("category",     i.Category.ToString()),
                new XAttribute("objectType",   i.ObjectType.ToString()),
                new XAttribute("objectHandle", i.ObjectHandle),
                new XAttribute("objectName",   i.ObjectName),
                i.Station.HasValue
                    ? new XAttribute("station",
                        i.Station.Value.ToString("F4", CultureInfo.InvariantCulture))
                    : null,
                !string.IsNullOrEmpty(i.LayerName)
                    ? new XAttribute("layer", i.LayerName)
                    : null,
                new XAttribute("detectedAt",   i.DetectedAt.ToString("o")),

                new XElement(ext + "Message",  new XCData(i.Message)),

                !string.IsNullOrEmpty(i.Detail)
                    ? new XElement(ext + "Detail", new XCData(i.Detail))
                    : null,

                i.Metadata.Count > 0
                    ? new XElement(ext + "Metadata",
                        i.Metadata.Select(kv =>
                            new XElement(ext + "Item",
                                new XAttribute("key",   kv.Key),
                                new XAttribute("value", kv.Value))))
                    : null);

            if (options.IncludeFixSuggestions && i.FixSuggestions.Count > 0)
            {
                el.Add(new XElement(ext + "FixSuggestions",
                    i.FixSuggestions.Select(f =>
                        new XElement(ext + "Fix",
                            new XAttribute("isAutomatable", f.IsAutomatable.ToString().ToLower()),
                            new XAttribute("estimatedManualMinutes", f.EstimatedManualMinutes),
                            f.RepairActionType != null
                                ? new XAttribute("repairActionType", f.RepairActionType)
                                : null,
                            new XElement(ext + "Title",       new XCData(f.Title)),
                            !string.IsNullOrEmpty(f.Description)
                                ? new XElement(ext + "Description", new XCData(f.Description))
                                : null))));
            }

            return el;
        }

        // ==================================================================
        // CSV Report
        // ==================================================================

        /// <summary>
        /// Generates a CSV report compatible with Microsoft Excel.
        /// One row per issue; columns include all key fields plus up to one fix suggestion.
        /// The first sheet (tab) contains issues; a second virtual sheet with the summary
        /// statistics is appended as a separate CSV section prefixed with blank rows.
        /// </summary>
        /// <param name="result">Populated analysis result.</param>
        /// <param name="options">Report options.</param>
        /// <returns>CSV string (UTF-8 BOM so Excel opens it correctly).</returns>
        public string GenerateCsv(AnalysisResult result, ReportOptions? options = null)
        {
            if (result is null) throw new ArgumentNullException(nameof(result));
            options ??= ReportOptions.Default;

            var summary = result.Summary ?? result.ComputeSummary();
            var sb = new StringBuilder();

            // UTF-8 BOM for Excel.
            sb.Append('﻿');

            // ---- Report metadata header ----
            sb.AppendLine(CsvRow("Civil 3D DWG Analysis Report"));
            sb.AppendLine(CsvRow("DWG File",        result.DwgFilePath));
            sb.AppendLine(CsvRow("File Size",        FormatFileSize(result.FileSizeBytes)));
            sb.AppendLine(CsvRow("Drawing Version",  result.DrawingVersion));
            sb.AppendLine(CsvRow("Civil 3D Version", result.Civil3DVersion));
            sb.AppendLine(CsvRow("Coordinate System",result.CoordinateSystem));
            sb.AppendLine(CsvRow("Analysis Started", result.StartedAt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"));
            sb.AppendLine(CsvRow("Duration (s)",     result.Duration.TotalSeconds.ToString("F1")));
            sb.AppendLine(CsvRow("Analyzer Version", result.AnalyzerVersion));
            sb.AppendLine(CsvRow("Health Score",     summary.HealthScore.ToString()));
            sb.AppendLine(CsvRow("Health Tier",      summary.HealthTier));
            sb.AppendLine();

            // ---- Summary section ----
            sb.AppendLine(CsvRow("SUMMARY STATISTICS"));
            sb.AppendLine(CsvRow("Metric", "Value"));
            sb.AppendLine(CsvRow("Total Issues",     summary.TotalIssues.ToString()));
            sb.AppendLine(CsvRow("Critical",         summary.CriticalCount.ToString()));
            sb.AppendLine(CsvRow("Warnings",         summary.WarningCount.ToString()));
            sb.AppendLine(CsvRow("Info",             summary.InfoCount.ToString()));
            sb.AppendLine(CsvRow("Affected Objects", summary.AffectedObjectCount.ToString()));
            sb.AppendLine();

            sb.AppendLine(CsvRow("ISSUES BY CATEGORY"));
            sb.AppendLine(CsvRow("Category", "Count"));
            foreach (var kv in summary.ByCategory.OrderByDescending(kv => kv.Value))
                sb.AppendLine(CsvRow(kv.Key.ToString(), kv.Value.ToString()));
            sb.AppendLine();

            sb.AppendLine(CsvRow("ISSUES BY OBJECT TYPE"));
            sb.AppendLine(CsvRow("Object Type", "Count"));
            foreach (var kv in summary.ByObjectType.OrderByDescending(kv => kv.Value))
                sb.AppendLine(CsvRow(kv.Key.ToString(), kv.Value.ToString()));
            sb.AppendLine();

            // ---- Issue detail section ----
            sb.AppendLine(CsvRow("ISSUE DETAIL"));

            // Column headers.
            var headers = new List<string>
            {
                "Issue ID", "Severity", "Category", "Object Type",
                "Object Handle", "Object Name", "Layer", "Station",
                "Message", "Detail"
            };
            if (options.IncludeFixSuggestions)
            {
                headers.Add("Fix Title");
                headers.Add("Fix Automatable");
                headers.Add("Fix Est. Minutes");
            }
            sb.AppendLine(CsvRow(headers.ToArray()));

            // Issue rows.
            var filteredIssues = result.Issues
                .Where(i => options.IncludeInfoItems || i.Severity != IssueSeverity.Info)
                .OrderBy(i => i.Severity)          // Critical first.
                .ThenBy(i => i.Category)
                .ThenBy(i => i.ObjectType)
                .ThenBy(i => i.ObjectName);

            foreach (var issue in filteredIssues)
            {
                string stationStr = issue.Station.HasValue
                    ? issue.Station.Value.ToString("F4", CultureInfo.InvariantCulture)
                    : string.Empty;

                var row = new List<string>
                {
                    issue.IssueId.ToString("N"),
                    issue.Severity.ToString(),
                    issue.Category.ToString(),
                    issue.ObjectType.ToString(),
                    issue.ObjectHandle,
                    issue.ObjectName,
                    issue.LayerName,
                    stationStr,
                    issue.Message,
                    issue.Detail
                };

                if (options.IncludeFixSuggestions)
                {
                    var firstFix = issue.FixSuggestions.FirstOrDefault();
                    row.Add(firstFix?.Title       ?? string.Empty);
                    row.Add(firstFix != null ? (firstFix.IsAutomatable ? "Yes" : "No") : string.Empty);
                    row.Add(firstFix != null ? firstFix.EstimatedManualMinutes.ToString() : string.Empty);
                }

                sb.AppendLine(CsvRow(row.ToArray()));
            }

            // ---- Object Inventory ----
            if (options.IncludeObjectInventory && result.ObjectInventory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(CsvRow("OBJECT INVENTORY"));
                sb.AppendLine(CsvRow("Handle", "Name", "Type", "Layer", "Issue Count", "Max Severity"));

                foreach (var rec in result.ObjectInventory
                    .OrderByDescending(r => r.MaxSeverity)
                    .ThenBy(r => r.Type))
                {
                    sb.AppendLine(CsvRow(
                        rec.Handle,
                        rec.Name,
                        rec.Type.ToString(),
                        rec.Layer,
                        rec.IssueCount.ToString(),
                        rec.MaxSeverity?.ToString() ?? "Clean"));
                }
            }

            return sb.ToString();
        }

        // ==================================================================
        // Convenience: write all formats to a folder
        // ==================================================================

        /// <summary>
        /// Writes all four report formats (HTML, JSON, XML, CSV) to the specified
        /// output folder.  Files are named using the DWG filename stem plus a
        /// timestamp so successive runs do not overwrite each other.
        /// </summary>
        /// <param name="result">Populated analysis result.</param>
        /// <param name="outputFolder">Destination folder (created if absent).</param>
        /// <param name="options">Report options.</param>
        /// <returns>Dictionary mapping format name to the written file path.</returns>
        public Dictionary<string, string> WriteAllReports(
            AnalysisResult result,
            string         outputFolder,
            ReportOptions? options = null)
        {
            if (result is null)        throw new ArgumentNullException(nameof(result));
            if (outputFolder is null)  throw new ArgumentNullException(nameof(outputFolder));

            Directory.CreateDirectory(outputFolder);
            options ??= ReportOptions.Default;

            string stem = Path.GetFileNameWithoutExtension(result.DwgFilePath);
            if (string.IsNullOrEmpty(stem)) stem = "analysis";
            string timestamp = result.StartedAt.ToString("yyyyMMdd_HHmmss");
            string prefix    = $"{stem}_{timestamp}";

            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // HTML
            string htmlPath = Path.Combine(outputFolder, $"{prefix}.html");
            File.WriteAllText(htmlPath, GenerateHtml(result, options), Encoding.UTF8);
            paths["HTML"] = htmlPath;

            // JSON
            string jsonPath = Path.Combine(outputFolder, $"{prefix}.json");
            File.WriteAllText(jsonPath, GenerateJson(result, options), Encoding.UTF8);
            paths["JSON"] = jsonPath;

            // XML
            string xmlPath = Path.Combine(outputFolder, $"{prefix}.xml");
            File.WriteAllText(xmlPath, GenerateXml(result, options), Encoding.UTF8);
            paths["XML"] = xmlPath;

            // CSV
            string csvPath = Path.Combine(outputFolder, $"{prefix}.csv");
            // CSV with BOM is written as-is (BOM is in the string already).
            File.WriteAllText(csvPath, GenerateCsv(result, options), new UTF8Encoding(true));
            paths["CSV"] = csvPath;

            return paths;
        }

        // ==================================================================
        // Private utilities
        // ==================================================================

        /// <summary>Builds a CSV-safe row string from column values.</summary>
        private static string CsvRow(params string[] values)
        {
            return string.Join(",", values.Select(CsvEscape));
        }

        /// <summary>Escapes a single CSV field value.</summary>
        private static string CsvEscape(string? value)
        {
            if (value is null) return string.Empty;
            // If value contains comma, double-quote, or newline → wrap in quotes.
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        /// <summary>HTML-encodes a string to prevent XSS.</summary>
        private static string HtmlEncode(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value
                .Replace("&",  "&amp;")
                .Replace("<",  "&lt;")
                .Replace(">",  "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'",  "&#39;");
        }

        /// <summary>Formats a byte count as a human-readable string (B / KB / MB / GB).</summary>
        private static string FormatFileSize(long bytes)
        {
            if (bytes < 0)      return "unknown";
            if (bytes < 1024)   return $"{bytes} B";
            if (bytes < 1_048_576)    return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1_073_741_824) return $"{bytes / 1_048_576.0:F1} MB";
            return $"{bytes / 1_073_741_824.0:F2} GB";
        }
    }
}
