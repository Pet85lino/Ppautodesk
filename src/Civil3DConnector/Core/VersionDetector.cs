using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace Autodesk.Civil3D.Connector.Core
{
    /// <summary>
    /// Represents a detected Civil 3D installation with version metadata and DLL paths.
    /// </summary>
    public sealed class Civil3DVersionInfo
    {
        /// <summary>Gets the major version year (e.g. 2025, 2026, 2027).</summary>
        public int VersionYear { get; init; }

        /// <summary>Gets the internal version number (e.g. 25.0, 26.0, 27.0).</summary>
        public Version InternalVersion { get; init; } = new Version(0, 0);

        /// <summary>Gets the installation root directory.</summary>
        public string InstallPath { get; init; } = string.Empty;

        /// <summary>Gets the full path to the primary Civil 3D application DLL.</summary>
        public string PrimaryDllPath { get; init; } = string.Empty;

        /// <summary>Gets the path to AeccXUiPipe DLL, if present.</summary>
        public string? AeccXUiPipeDllPath { get; init; }

        /// <summary>Gets the path to AeccXUiRoadway DLL, if present.</summary>
        public string? AeccXUiRoadwayDllPath { get; init; }

        /// <summary>Gets a read-only dictionary of all resolved Civil 3D DLL paths keyed by assembly name.</summary>
        public IReadOnlyDictionary<string, string> ResolvedDlls { get; init; }
            = new Dictionary<string, string>();

        /// <summary>Gets the detection source that successfully identified this version.</summary>
        public VersionDetectionSource DetectionSource { get; init; }

        /// <summary>Gets whether this version is fully supported by the connector.</summary>
        public bool IsSupported =>
            VersionYear is >= Civil3DVersionDetector.MinSupportedYear
                       and <= Civil3DVersionDetector.MaxSupportedYear;

        /// <inheritdoc/>
        public override string ToString() =>
            $"Civil 3D {VersionYear} (internal {InternalVersion}) [{DetectionSource}] at \"{InstallPath}\"";
    }

    /// <summary>Enumerates the sources that can be used to detect a Civil 3D installation.</summary>
    public enum VersionDetectionSource
    {
        /// <summary>Version resolved from the Windows registry.</summary>
        Registry,
        /// <summary>Version resolved by scanning well-known DLL paths on disk.</summary>
        DllScan,
        /// <summary>Version resolved from environment variables.</summary>
        EnvironmentVariable,
        /// <summary>Version resolved from a currently loaded assembly in the AppDomain.</summary>
        LoadedAssembly,
        /// <summary>Detection source is unknown or composite.</summary>
        Unknown
    }

    /// <summary>
    /// Detects which version(s) of Autodesk Civil 3D are installed by interrogating
    /// the Windows registry, well-known DLL locations, and environment variables.
    /// Thread-safe; results are cached after the first scan.
    /// </summary>
    public sealed class Civil3DVersionDetector : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────────
        // Constants
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>Earliest year supported by this connector.</summary>
        public const int MinSupportedYear = 2025;

        /// <summary>Latest year supported by this connector.</summary>
        public const int MaxSupportedYear = 2027;

        private const string RegistryBaseKey =
            @"SOFTWARE\Autodesk\AutoCAD";

        private static readonly IReadOnlyDictionary<int, string> YearToInternalVersion =
            new Dictionary<int, string>
            {
                { 2025, "25.0" },
                { 2026, "26.0" },
                { 2027, "27.0" }
            };

        /// <summary>
        /// Known Civil 3D sub-keys appended after the internal version key in the registry.
        /// Autodesk uses "UES" → civil engineering suite style keys in some releases.
        /// </summary>
        private static readonly string[] CivilSubKeys =
        {
            @"Applications\Autodesk Civil 3D",
            @"Applications\Civil 3D"
        };

        /// <summary>Assembly names that must exist for a valid Civil 3D installation.</summary>
        private static readonly string[] RequiredAssemblies =
        {
            "Autodesk.Civil.ApplicationServices",
            "Autodesk.Civil.DatabaseServices",
            "AeccXUiLand"
        };

        /// <summary>Optional assembly names that are catalogued when present.</summary>
        private static readonly string[] OptionalAssemblies =
        {
            "AeccXUiPipe",
            "AeccXUiRoadway",
            "AeccXUiSurvey",
            "AeccXUiGrading",
            "Autodesk.Civil.DatabaseServices.Styles",
            "Autodesk.Civil.Runtime",
            "AcMgd",
            "AcDbMgd",
            "AcCoreMgd"
        };

        // ──────────────────────────────────────────────────────────────────────────
        // Fields
        // ──────────────────────────────────────────────────────────────────────────

        private readonly object _lock = new();
        private List<Civil3DVersionInfo>? _cachedResults;
        private bool _disposed;

        // ──────────────────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all detected Civil 3D installations. Results are cached; call
        /// <see cref="InvalidateCache"/> to force a rescan.
        /// </summary>
        /// <returns>An ordered list of detected installations (newest first).</returns>
        /// <exception cref="PlatformNotSupportedException">
        /// Thrown when executed on a non-Windows platform where registry detection
        /// is unavailable and no environment overrides are set.
        /// </exception>
        public IReadOnlyList<Civil3DVersionInfo> DetectAll()
        {
            lock (_lock)
            {
                if (_cachedResults is not null)
                    return _cachedResults.AsReadOnly();

                _cachedResults = PerformDetection();
                return _cachedResults.AsReadOnly();
            }
        }

        /// <summary>
        /// Returns the best (newest supported) Civil 3D installation, or <c>null</c>
        /// if none is found.
        /// </summary>
        public Civil3DVersionInfo? DetectBest()
        {
            return DetectAll()
                .Where(v => v.IsSupported)
                .OrderByDescending(v => v.VersionYear)
                .FirstOrDefault();
        }

        /// <summary>
        /// Returns the Civil 3D installation for the specified <paramref name="year"/>,
        /// or <c>null</c> if that version is not installed.
        /// </summary>
        /// <param name="year">The version year (e.g. 2025).</param>
        public Civil3DVersionInfo? DetectByYear(int year)
        {
            return DetectAll().FirstOrDefault(v => v.VersionYear == year);
        }

        /// <summary>Clears the cached detection results so the next call re-scans.</summary>
        public void InvalidateCache()
        {
            lock (_lock)
            {
                _cachedResults = null;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Nothing unmanaged to release; method provided for future extensibility
            // and consistent IDisposable usage patterns.
            GC.SuppressFinalize(this);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Detection orchestration
        // ──────────────────────────────────────────────────────────────────────────

        private List<Civil3DVersionInfo> PerformDetection()
        {
            var results = new Dictionary<int, Civil3DVersionInfo>();

            // 1. Registry (most authoritative on Windows)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var info in ScanRegistry())
                    results.TryAdd(info.VersionYear, info);
            }

            // 2. DLL scan (works cross-platform or when registry is unavailable)
            foreach (var info in ScanDlls())
                results.TryAdd(info.VersionYear, info);

            // 3. Environment variables (CI / override scenarios)
            foreach (var info in ScanEnvironmentVariables())
                results.TryAdd(info.VersionYear, info);

            // 4. Loaded assemblies in current AppDomain (running inside Civil 3D)
            foreach (var info in ScanLoadedAssemblies())
                results.TryAdd(info.VersionYear, info);

            return results.Values
                .OrderByDescending(v => v.VersionYear)
                .ToList();
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Strategy 1 – Registry
        // ──────────────────────────────────────────────────────────────────────────

#if WINDOWS
        private IEnumerable<Civil3DVersionInfo> ScanRegistry()
        {
            foreach (var (year, internalVer) in YearToInternalVersion)
            {
                Civil3DVersionInfo? info = null;

                // Try HKLM (per-machine install)
                info ??= TryReadRegistryHive(
                    Registry.LocalMachine, year, internalVer,
                    RegistryView.Registry64);

                // Try HKCU (per-user install)
                info ??= TryReadRegistryHive(
                    Registry.CurrentUser, year, internalVer,
                    RegistryView.Registry64);

                if (info is not null)
                    yield return info;
            }
        }

        private static Civil3DVersionInfo? TryReadRegistryHive(
            RegistryKey hive,
            int year,
            string internalVer,
            RegistryView view)
        {
            try
            {
                string autocadVersionKey =
                    $@"{RegistryBaseKey}\R{internalVer}";

                using RegistryKey? baseKey =
                    RegistryKey.OpenBaseKey(
                        hive == Registry.LocalMachine
                            ? RegistryHive.LocalMachine
                            : RegistryHive.CurrentUser,
                        view);

                using RegistryKey? autocadKey =
                    baseKey?.OpenSubKey(autocadVersionKey);

                if (autocadKey is null) return null;

                // The "ACAD" value points to the executable directory.
                string? acadPath = autocadKey.GetValue("AcadLocation") as string
                                ?? autocadKey.GetValue("") as string;

                foreach (string civilSubKey in CivilSubKeys)
                {
                    using RegistryKey? civilKey =
                        autocadKey.OpenSubKey(civilSubKey);

                    if (civilKey is null) continue;

                    string? installDir =
                        civilKey.GetValue("INSTALLDIR") as string
                     ?? civilKey.GetValue("InstallDir") as string
                     ?? acadPath;

                    if (string.IsNullOrWhiteSpace(installDir)) continue;
                    installDir = installDir.TrimEnd('\\', '/');

                    if (!Directory.Exists(installDir)) continue;

                    var dlls = ResolveDlls(installDir, year);
                    if (dlls is null) continue;

                    return BuildVersionInfo(
                        year, internalVer, installDir, dlls,
                        VersionDetectionSource.Registry);
                }
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException
                or System.Security.SecurityException
                or IOException)
            {
                // Registry access denied – silently skip.
            }

            return null;
        }
#else
        // Non-Windows stub so the file compiles on Linux/macOS build agents.
        private static IEnumerable<Civil3DVersionInfo> ScanRegistry()
            => Enumerable.Empty<Civil3DVersionInfo>();
#endif

        // ──────────────────────────────────────────────────────────────────────────
        // Strategy 2 – DLL scan
        // ──────────────────────────────────────────────────────────────────────────

        private IEnumerable<Civil3DVersionInfo> ScanDlls()
        {
            // Build candidate root directories for each supported year.
            foreach (var (year, internalVer) in YearToInternalVersion)
            {
                foreach (string candidate in GetCandidateInstallDirs(year, internalVer))
                {
                    if (!Directory.Exists(candidate)) continue;

                    var dlls = ResolveDlls(candidate, year);
                    if (dlls is null) continue;

                    yield return BuildVersionInfo(
                        year, internalVer, candidate, dlls,
                        VersionDetectionSource.DllScan);

                    break; // First valid path wins for this year.
                }
            }
        }

        private static IEnumerable<string> GetCandidateInstallDirs(int year, string internalVer)
        {
            // Standard Autodesk install paths on Windows
            string[] programFileDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"C:\Program Files\Autodesk",
                @"D:\Program Files\Autodesk"
            };

            foreach (string root in programFileDirs.Where(d => !string.IsNullOrEmpty(d)))
            {
                yield return Path.Combine(root, $"AutoCAD {year}");
                yield return Path.Combine(root, $"Autodesk\\AutoCAD {year}");
                yield return Path.Combine(root, $"AutoCAD Civil 3D {year}");
                yield return Path.Combine(root, $"Autodesk\\AutoCAD Civil 3D {year}");
                yield return Path.Combine(root, $"Civil 3D {year}");
            }

            // Linux/macOS Wine-prefix paths for CI
            yield return $"/opt/autodesk/civil3d{year}";
            yield return $"/usr/local/autodesk/civil3d{year}";
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Strategy 3 – Environment variables
        // ──────────────────────────────────────────────────────────────────────────

        private static IEnumerable<Civil3DVersionInfo> ScanEnvironmentVariables()
        {
            foreach (var (year, internalVer) in YearToInternalVersion)
            {
                // Conventional names: CIVIL3D_2025_PATH, AUTOCAD_2025_PATH, etc.
                string[] varNames =
                {
                    $"CIVIL3D_{year}_PATH",
                    $"AUTOCAD_{year}_PATH",
                    $"ACA_{year}_PATH"
                };

                foreach (string varName in varNames)
                {
                    string? envPath = Environment.GetEnvironmentVariable(varName)
                                   ?? Environment.GetEnvironmentVariable(
                                          varName,
                                          EnvironmentVariableTarget.Machine)
                                   ?? Environment.GetEnvironmentVariable(
                                          varName,
                                          EnvironmentVariableTarget.User);

                    if (string.IsNullOrWhiteSpace(envPath)) continue;
                    if (!Directory.Exists(envPath)) continue;

                    var dlls = ResolveDlls(envPath, year);
                    if (dlls is null) continue;

                    yield return BuildVersionInfo(
                        year, internalVer, envPath, dlls,
                        VersionDetectionSource.EnvironmentVariable);

                    break; // Found a good path for this year.
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Strategy 4 – Loaded assemblies
        // ──────────────────────────────────────────────────────────────────────────

        private static IEnumerable<Civil3DVersionInfo> ScanLoadedAssemblies()
        {
            // If we're running inside Civil 3D, the assemblies are already loaded.
            Assembly[] loadedAssemblies;
            try
            {
                loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch
            {
                yield break;
            }

            // Look for the canonical Civil 3D identity assembly.
            Assembly? civilAsm = loadedAssemblies.FirstOrDefault(a =>
                a.GetName().Name?.StartsWith(
                    "Autodesk.Civil.ApplicationServices",
                    StringComparison.OrdinalIgnoreCase) == true);

            if (civilAsm is null) yield break;

            Version? asmVer = civilAsm.GetName().Version;
            if (asmVer is null) yield break;

            int year = InternalVersionToYear(asmVer.Major);
            if (year == 0) yield break;

            string installPath =
                Path.GetDirectoryName(civilAsm.Location) ?? string.Empty;

            var dlls = ResolveDlls(installPath, year);
            if (dlls is null) yield break;

            yield return BuildVersionInfo(
                year,
                $"{asmVer.Major}.{asmVer.Minor}",
                installPath,
                dlls,
                VersionDetectionSource.LoadedAssembly);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Helpers
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Scans <paramref name="dir"/> for the required and optional Civil 3D
        /// assemblies, returning a populated dictionary or <c>null</c> if any
        /// required assembly is missing.
        /// </summary>
        private static Dictionary<string, string>? ResolveDlls(string dir, int year)
        {
            var resolved = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (string asmName in RequiredAssemblies)
            {
                string candidate = Path.Combine(dir, $"{asmName}.dll");
                if (!File.Exists(candidate))
                    return null; // Missing required – disqualify path.

                resolved[asmName] = candidate;
            }

            foreach (string asmName in OptionalAssemblies)
            {
                string candidate = Path.Combine(dir, $"{asmName}.dll");
                if (File.Exists(candidate))
                    resolved[asmName] = candidate;
            }

            return resolved;
        }

        private static Civil3DVersionInfo BuildVersionInfo(
            int year,
            string internalVer,
            string installDir,
            Dictionary<string, string> dlls,
            VersionDetectionSource source)
        {
            dlls.TryGetValue("Autodesk.Civil.ApplicationServices", out string? primaryDll);
            dlls.TryGetValue("AeccXUiPipe", out string? pipeDll);
            dlls.TryGetValue("AeccXUiRoadway", out string? roadwayDll);

            return new Civil3DVersionInfo
            {
                VersionYear             = year,
                InternalVersion         = Version.TryParse(internalVer, out var v) ? v : new Version(0, 0),
                InstallPath             = installDir,
                PrimaryDllPath          = primaryDll ?? string.Empty,
                AeccXUiPipeDllPath      = pipeDll,
                AeccXUiRoadwayDllPath   = roadwayDll,
                ResolvedDlls            = dlls,
                DetectionSource         = source
            };
        }

        private static int InternalVersionToYear(int major) => major switch
        {
            25 => 2025,
            26 => 2026,
            27 => 2027,
            _  => 0
        };
    }
}
