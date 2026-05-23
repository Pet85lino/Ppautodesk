using System;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using Civil3DConnector.Analyzers;
using Civil3DConnector.Generators;
using Civil3DConnector.Validators;

namespace Civil3DConnector.UI
{
    /// <summary>
    /// Palette panel that hosts the Civil 3D Intelligent Connector UI inside AutoCAD.
    /// Registered as a PaletteSet to dock alongside other AutoCAD palettes.
    /// </summary>
    public class ConnectorPaletteSet : IDisposable
    {
        private static PaletteSet? _paletteSet;
        private static ConnectorPanel? _panel;
        private static readonly Guid PaletteGuid = new Guid("C3DConn-2025-PALETTE-GUID");
        private bool _disposed;

        public static void ShowPalette()
        {
            if (_paletteSet == null)
            {
                _paletteSet = new PaletteSet("Civil3D Intelligent Connector", PaletteGuid)
                {
                    Size = new Size(380, 700),
                    DockEnabled = DockSides.Left | DockSides.Right,
                    MinimumSize = new Size(320, 400)
                };

                _panel = new ConnectorPanel();
                _paletteSet.Add("Conector", _panel);
            }

            _paletteSet.Visible = true;
        }

        public static void HidePalette()
        {
            if (_paletteSet != null)
                _paletteSet.Visible = false;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _paletteSet?.Dispose();
                _paletteSet = null;
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }

    /// <summary>
    /// WinForms UserControl that renders the connector UI within the AutoCAD palette.
    /// </summary>
    public class ConnectorPanel : UserControl
    {
        private TabControl _tabs = null!;
        private TabPage _analyzeTab = null!;
        private TabPage _generateTab = null!;
        private TabPage _validateTab = null!;
        private TabPage _aiTab = null!;

        // Analyze Tab controls
        private Button _btnAnalyze = null!;
        private CheckBox _cbCorruption = null!;
        private CheckBox _cbReferences = null!;
        private CheckBox _cbStyles = null!;
        private CheckBox _cbNetworks = null!;
        private RichTextBox _rtbAnalyzeResults = null!;

        // Validate Tab controls
        private ComboBox _cmbStandard = null!;
        private Button _btnValidate = null!;
        private DataGridView _dgvViolations = null!;

        // Generate Tab controls
        private ComboBox _cmbWorkflow = null!;
        private Button _btnGenDynamo = null!;
        private Button _btnGenPlugin = null!;
        private RichTextBox _rtbGeneratedCode = null!;

        // AI Tab controls
        private TextBox _txtAiCommand = null!;
        private Button _btnAiProcess = null!;
        private RichTextBox _rtbAiResponse = null!;

        public ConnectorPanel()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            SuspendLayout();
            BackColor = Color.FromArgb(45, 45, 48);
            ForeColor = Color.White;
            Size = new Size(360, 680);

            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 8.5f),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White
            };

            BuildAnalyzeTab();
            BuildValidateTab();
            BuildGenerateTab();
            BuildAiTab();

            _tabs.TabPages.AddRange(new[] { _analyzeTab, _validateTab, _generateTab, _aiTab });
            Controls.Add(_tabs);

            ResumeLayout(false);
        }

        private void BuildAnalyzeTab()
        {
            _analyzeTab = new TabPage("Análisis") { BackColor = Color.FromArgb(37, 37, 38) };

            var lblTitle = new Label
            {
                Text = "Análisis de DWG",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 122, 204),
                Location = new Point(8, 8), AutoSize = true
            };

            _cbCorruption = new CheckBox { Text = "Corrupción de objetos", Checked = true, ForeColor = Color.White, Location = new Point(8, 35), AutoSize = true };
            _cbReferences = new CheckBox { Text = "Referencias faltantes", Checked = true, ForeColor = Color.White, Location = new Point(8, 58), AutoSize = true };
            _cbStyles = new CheckBox { Text = "Estilos y etiquetas", Checked = true, ForeColor = Color.White, Location = new Point(8, 81), AutoSize = true };
            _cbNetworks = new CheckBox { Text = "Redes y corredores", Checked = true, ForeColor = Color.White, Location = new Point(8, 104), AutoSize = true };

            _btnAnalyze = new Button
            {
                Text = "Analizar DWG",
                Location = new Point(8, 132), Size = new Size(140, 28),
                BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnAnalyze.Click += OnAnalyzeClick;

            _rtbAnalyzeResults = new RichTextBox
            {
                Location = new Point(8, 170), Size = new Size(330, 320),
                BackColor = Color.FromArgb(28, 28, 28), ForeColor = Color.LightGray,
                ReadOnly = true, Font = new Font("Consolas", 8f),
                Text = "Resultado del análisis aparecerá aquí..."
            };

            _analyzeTab.Controls.AddRange(new Control[]
                { lblTitle, _cbCorruption, _cbReferences, _cbStyles, _cbNetworks, _btnAnalyze, _rtbAnalyzeResults });
        }

        private void BuildValidateTab()
        {
            _validateTab = new TabPage("Validar") { BackColor = Color.FromArgb(37, 37, 38) };

            var lblTitle = new Label
            {
                Text = "Validación Normativa Ecuador",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 122, 204),
                Location = new Point(8, 8), AutoSize = true
            };

            var lblStd = new Label { Text = "Normativa:", ForeColor = Color.White, Location = new Point(8, 38), AutoSize = true };

            _cmbStandard = new ComboBox
            {
                Location = new Point(80, 35), Width = 200,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbStandard.Items.AddRange(new object[] { "Todas", "Interagua", "Amagua", "MTOP", "NEC", "NTE INEN" });
            _cmbStandard.SelectedIndex = 0;

            _btnValidate = new Button
            {
                Text = "Validar",
                Location = new Point(8, 65), Size = new Size(120, 28),
                BackColor = Color.FromArgb(76, 153, 0), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnValidate.Click += OnValidateClick;

            _dgvViolations = new DataGridView
            {
                Location = new Point(8, 103), Size = new Size(330, 380),
                BackgroundColor = Color.FromArgb(28, 28, 28),
                ForeColor = Color.White, GridColor = Color.FromArgb(60, 60, 60),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false, AllowUserToAddRows = false,
                ReadOnly = true
            };
            _dgvViolations.Columns.Add("Object", "Objeto");
            _dgvViolations.Columns.Add("Standard", "Norma");
            _dgvViolations.Columns.Add("Message", "Violación");

            _validateTab.Controls.AddRange(new Control[]
                { lblTitle, lblStd, _cmbStandard, _btnValidate, _dgvViolations });
        }

        private void BuildGenerateTab()
        {
            _generateTab = new TabPage("Generar") { BackColor = Color.FromArgb(37, 37, 38) };

            var lblTitle = new Label
            {
                Text = "Generador de Código",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 122, 204),
                Location = new Point(8, 8), AutoSize = true
            };

            var lblWf = new Label { Text = "Workflow:", ForeColor = Color.White, Location = new Point(8, 38), AutoSize = true };

            _cmbWorkflow = new ComboBox
            {
                Location = new Point(70, 35), Width = 210,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbWorkflow.Items.AddRange(new object[]
                { "Corredor", "Red Tuberías", "Superficie", "Alineamiento", "Perfil", "Parcelas" });
            _cmbWorkflow.SelectedIndex = 0;

            _btnGenDynamo = new Button
            {
                Text = "Generar Dynamo (.dyn)",
                Location = new Point(8, 65), Size = new Size(160, 28),
                BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnGenDynamo.Click += OnGenDynamoClick;

            _btnGenPlugin = new Button
            {
                Text = "Generar Plugin .NET",
                Location = new Point(178, 65), Size = new Size(140, 28),
                BackColor = Color.FromArgb(153, 0, 76), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnGenPlugin.Click += OnGenPluginClick;

            _rtbGeneratedCode = new RichTextBox
            {
                Location = new Point(8, 103), Size = new Size(330, 380),
                BackColor = Color.FromArgb(28, 28, 28), ForeColor = Color.LightGray,
                ReadOnly = true, Font = new Font("Consolas", 8f),
                Text = "El código generado aparecerá aquí..."
            };

            _generateTab.Controls.AddRange(new Control[]
                { lblTitle, lblWf, _cmbWorkflow, _btnGenDynamo, _btnGenPlugin, _rtbGeneratedCode });
        }

        private void BuildAiTab()
        {
            _aiTab = new TabPage("IA") { BackColor = Color.FromArgb(37, 37, 38) };

            var lblTitle = new Label
            {
                Text = "Asistente IA - Civil 3D",
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 122, 204),
                Location = new Point(8, 8), AutoSize = true
            };

            var lblHint = new Label
            {
                Text = "Instrucción en español o inglés:",
                ForeColor = Color.Silver, Location = new Point(8, 35), AutoSize = true
            };

            _txtAiCommand = new TextBox
            {
                Location = new Point(8, 55), Width = 285,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
                Text = "crear corredor de 500m en alineamiento..."
            };

            _btnAiProcess = new Button
            {
                Text = "Procesar",
                Location = new Point(300, 53), Size = new Size(70, 24),
                BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnAiProcess.Click += OnAiProcessClick;

            _rtbAiResponse = new RichTextBox
            {
                Location = new Point(8, 88), Size = new Size(330, 480),
                BackColor = Color.FromArgb(28, 28, 28), ForeColor = Color.LightGray,
                ReadOnly = true, Font = new Font("Consolas", 8f),
                Text = "Respuesta del asistente aparecerá aquí..."
            };

            _aiTab.Controls.AddRange(new Control[]
                { lblTitle, lblHint, _txtAiCommand, _btnAiProcess, _rtbAiResponse });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Event Handlers
        // ─────────────────────────────────────────────────────────────────────

        private void OnAnalyzeClick(object? sender, EventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) { ShowInfo("No hay documento activo."); return; }

            _btnAnalyze.Enabled = false;
            _rtbAnalyzeResults.Clear();
            _rtbAnalyzeResults.AppendText("Analizando...\n");

            try
            {
                using var analyzer = new DwgAnalyzer(doc);
                var result = analyzer.RunFullAnalysis();

                _rtbAnalyzeResults.Clear();
                AppendColored(_rtbAnalyzeResults, $"Análisis completado\n", Color.LimeGreen);
                AppendColored(_rtbAnalyzeResults, $"Objetos escaneados: {result.TotalObjectsScanned}\n", Color.White);
                AppendColored(_rtbAnalyzeResults, $"Críticos: {result.CriticalIssues.Count}\n", Color.OrangeRed);
                AppendColored(_rtbAnalyzeResults, $"Advertencias: {result.Warnings.Count}\n", Color.Yellow);
                AppendColored(_rtbAnalyzeResults, $"Info: {result.InfoItems.Count}\n", Color.CornflowerBlue);
                _rtbAnalyzeResults.AppendText("\n");

                foreach (var issue in result.CriticalIssues)
                    AppendColored(_rtbAnalyzeResults, $"[CRÍTICO] {issue.ObjectName}: {issue.Description}\n", Color.OrangeRed);
                foreach (var issue in result.Warnings)
                    AppendColored(_rtbAnalyzeResults, $"[AVISO] {issue.ObjectName}: {issue.Description}\n", Color.Yellow);
            }
            catch (Exception ex)
            {
                AppendColored(_rtbAnalyzeResults, $"Error: {ex.Message}\n", Color.Red);
            }
            finally
            {
                _btnAnalyze.Enabled = true;
            }
        }

        private void OnValidateClick(object? sender, EventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) { ShowInfo("No hay documento activo."); return; }

            _dgvViolations.Rows.Clear();
            _btnValidate.Enabled = false;

            try
            {
                var standardStr = _cmbStandard.SelectedItem?.ToString() ?? "Todas";
                var standard = standardStr switch
                {
                    "Interagua" => ValidatorStandard.Interagua,
                    "Amagua"    => ValidatorStandard.Amagua,
                    "MTOP"      => ValidatorStandard.MTOP,
                    "NEC"       => ValidatorStandard.NEC,
                    _           => ValidatorStandard.All
                };

                using var validator = new EcuadorianStandardsValidator(doc);
                var violations = validator.ValidateAll(standard);

                foreach (var v in violations)
                    _dgvViolations.Rows.Add(v.ObjectName, v.Standard, v.ViolationMessage);

                if (violations.Count == 0)
                    _dgvViolations.Rows.Add("—", "—", "Sin violaciones ✓");
            }
            catch (Exception ex)
            {
                _dgvViolations.Rows.Add("ERROR", "", ex.Message);
            }
            finally
            {
                _btnValidate.Enabled = true;
            }
        }

        private void OnGenDynamoClick(object? sender, EventArgs e)
        {
            var workflow = _cmbWorkflow.SelectedItem?.ToString() ?? "Corredor";
            _btnGenDynamo.Enabled = false;
            _rtbGeneratedCode.Clear();

            try
            {
                var generator = new DynamoScriptGenerator();
                string json = workflow switch
                {
                    "Corredor"       => generator.GenerateCorridorScript(),
                    "Red Tuberías"   => generator.GeneratePipeNetworkScript(),
                    "Superficie"     => generator.GenerateSurfaceScript(),
                    "Alineamiento"   => generator.GenerateAlignmentScript(),
                    "Perfil"         => generator.GenerateProfileScript(),
                    _                => generator.GenerateCorridorScript()
                };

                AppendColored(_rtbGeneratedCode, $"// Script Dynamo: {workflow}\n", Color.LimeGreen);
                _rtbGeneratedCode.AppendText(json);

                // Auto-save
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    string path = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(doc.Name) ?? System.IO.Path.GetTempPath(),
                        $"{workflow.Replace(" ", "_")}_{DateTime.Now:HHmm}.dyn");
                    System.IO.File.WriteAllText(path, json, System.Text.Encoding.UTF8);
                    AppendColored(_rtbGeneratedCode, $"\n\n// Guardado: {path}", Color.CornflowerBlue);
                }
            }
            catch (Exception ex)
            {
                AppendColored(_rtbGeneratedCode, $"Error: {ex.Message}", Color.Red);
            }
            finally
            {
                _btnGenDynamo.Enabled = true;
            }
        }

        private void OnGenPluginClick(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Carpeta para el proyecto plugin",
                ShowNewFolderButton = true
            };

            if (dlg.ShowDialog() != DialogResult.OK) return;

            _btnGenPlugin.Enabled = false;
            _rtbGeneratedCode.Clear();

            try
            {
                var generator = new DotNetPluginGenerator();
                var opts = new PluginGeneratorOptions
                {
                    PluginName = "MiPlugin_Civil3D",
                    TargetVersions = new[] { "2025", "2026", "2027" },
                    IncludeRibbon = true,
                    IncludePalette = true,
                    OutputDirectory = dlg.SelectedPath
                };

                generator.GenerateProject(opts);
                AppendColored(_rtbGeneratedCode, $"Proyecto generado en:\n{dlg.SelectedPath}\n\nAbrir .sln en Visual Studio.", Color.LimeGreen);
            }
            catch (Exception ex)
            {
                AppendColored(_rtbGeneratedCode, $"Error: {ex.Message}", Color.Red);
            }
            finally
            {
                _btnGenPlugin.Enabled = true;
            }
        }

        private void OnAiProcessClick(object? sender, EventArgs e)
        {
            string command = _txtAiCommand.Text.Trim();
            if (string.IsNullOrEmpty(command)) return;

            _btnAiProcess.Enabled = false;
            _rtbAiResponse.Clear();

            try
            {
                var nlp = new NaturalLanguageProcessor();
                var plan = nlp.Parse(command);

                AppendColored(_rtbAiResponse, $"Interpretación: {plan.IntentDescription}\n", Color.LimeGreen);
                AppendColored(_rtbAiResponse, $"Confianza: {plan.Confidence:P0}\n", Color.Yellow);
                _rtbAiResponse.AppendText("\nWorkflow:\n");

                foreach (var step in plan.Steps)
                    _rtbAiResponse.AppendText($"  {step.Order}. {step.Description}\n");

                _rtbAiResponse.AppendText("\nAPIs requeridas:\n");
                foreach (var api in plan.RequiredApis)
                    AppendColored(_rtbAiResponse, $"  • {api}\n", Color.CornflowerBlue);

                if (!string.IsNullOrEmpty(plan.GeneratedCode))
                {
                    _rtbAiResponse.AppendText("\n--- Código generado ---\n");
                    AppendColored(_rtbAiResponse, plan.GeneratedCode, Color.LightGray);
                }
            }
            catch (Exception ex)
            {
                AppendColored(_rtbAiResponse, $"Error: {ex.Message}", Color.Red);
            }
            finally
            {
                _btnAiProcess.Enabled = true;
            }
        }

        private static void AppendColored(RichTextBox rtb, string text, Color color)
        {
            int start = rtb.TextLength;
            rtb.AppendText(text);
            rtb.Select(start, text.Length);
            rtb.SelectionColor = color;
            rtb.SelectionLength = 0;
        }

        private static void ShowInfo(string message)
        {
            MessageBox.Show(message, "Civil3D Connector", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
