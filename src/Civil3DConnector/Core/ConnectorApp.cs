using System;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Civil3DConnector.UI;

[assembly: ExtensionApplication(typeof(Civil3DConnector.Core.ConnectorApp))]

namespace Civil3DConnector.Core
{
    /// <summary>
    /// AutoCAD extension application entry point.
    /// Loaded by Civil 3D via NETLOAD or autoload registry key.
    /// </summary>
    public class ConnectorApp : IExtensionApplication
    {
        private static Civil3DVersionDetector? _detector;

        /// <summary>Called when the DLL is loaded by AutoCAD/Civil 3D.</summary>
        public void Initialize()
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                var ed = doc?.Editor;

                ed?.WriteMessage("\n╔══════════════════════════════════════════════════╗");
                ed?.WriteMessage("\n║  Civil 3D Intelligent Connector v2.0 cargado    ║");
                ed?.WriteMessage("\n║  Escribe CIVILAYUDA para ver comandos           ║");
                ed?.WriteMessage("\n╚══════════════════════════════════════════════════╝\n");

                // Detect installed Civil 3D version
                _detector = new Civil3DVersionDetector();
                var version = _detector.DetectInstalledVersion();
                ed?.WriteMessage($"\n  Versión detectada: Civil 3D {version?.VersionYear ?? "desconocida"}");
                ed?.WriteMessage($"\n  Ruta instalación: {version?.InstallPath ?? "no encontrada"}");

                // Register document events
                Application.DocumentManager.DocumentCreated += OnDocumentCreated;
                Application.DocumentManager.DocumentToBeDestroyed += OnDocumentDestroyed;

                ed?.WriteMessage("\n  Conector inicializado correctamente.\n");
            }
            catch (Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    ?.WriteMessage($"\n[Civil3D Connector] Error al inicializar: {ex.Message}");
            }
        }

        /// <summary>Called when the DLL is unloaded.</summary>
        public void Terminate()
        {
            try
            {
                Application.DocumentManager.DocumentCreated -= OnDocumentCreated;
                Application.DocumentManager.DocumentToBeDestroyed -= OnDocumentDestroyed;
                ConnectorPaletteSet.HidePalette();
                _detector?.Dispose();
            }
            catch
            {
                // Ignore errors during unload
            }
        }

        private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
        {
            e.Document.Editor.WriteMessage("\n[Civil3D Connector] Nuevo documento detectado. Use CIVILANALYZE para analizar.");
        }

        private static void OnDocumentDestroyed(object? sender, DocumentCollectionEventArgs e)
        {
            // Clean up any per-document state here
        }
    }
}
