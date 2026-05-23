using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Autodesk.Civil3D.Connector.Core
{
    // ──────────────────────────────────────────────────────────────────────────────
    // Public data types
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents a resolved Civil 3D API method that can be invoked without
    /// knowing the exact version-specific type or method signature at compile time.
    /// </summary>
    public sealed class ResolvedApiMethod
    {
        /// <summary>Gets the reflected method info.</summary>
        public MethodInfo Method { get; init; } = null!;

        /// <summary>Gets the declaring type on which the method was found.</summary>
        public Type DeclaringType { get; init; } = null!;

        /// <summary>Gets the Civil 3D version year for which this method was resolved.</summary>
        public int VersionYear { get; init; }

        /// <summary>
        /// Invokes the method on <paramref name="instance"/> with the supplied
        /// <paramref name="args"/> and returns the result.
        /// Exceptions are rethrown preserving the original stack trace.
        /// </summary>
        public object? Invoke(object? instance, params object?[] args)
        {
            try
            {
                return Method.Invoke(instance, args);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable – satisfies compiler
            }
        }
    }

    /// <summary>
    /// Describes a single version-specific type mapping entry used by
    /// <see cref="ApiVersionAdapter"/> when resolving type names.
    /// </summary>
    public sealed record ApiTypeMapping(
        int VersionYear,
        string LogicalName,
        string FullTypeName,
        string AssemblyName);

    // ──────────────────────────────────────────────────────────────────────────────
    // ApiVersionAdapter
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adapts Civil 3D API calls across versions 2025, 2026, and 2027 using
    /// reflection-based late binding.  All loaded assemblies are cached so
    /// that each DLL is loaded only once per <see cref="ApiVersionAdapter"/>
    /// instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage pattern:
    /// <code>
    ///   var detector  = new Civil3DVersionDetector();
    ///   var versionInfo = detector.DetectBest()!;
    ///   using var adapter = new ApiVersionAdapter(versionInfo);
    ///   var method = adapter.ResolveMethod(
    ///       "CivilApplication", "GetActiveCivilDocument");
    ///   object? doc = method.Invoke(null);
    /// </code>
    /// </para>
    /// </remarks>
    public sealed class ApiVersionAdapter : IDisposable
    {
        // ── Constants ──────────────────────────────────────────────────────────────

        private const string CivilAppTypeName   = "Autodesk.Civil.ApplicationServices.CivilApplication";
        private const string CivilDocTypeName   = "Autodesk.Civil.ApplicationServices.CivilDocument";
        private const string AlignmentTypeName  = "Autodesk.Civil.DatabaseServices.Alignment";
        private const string ProfileTypeName    = "Autodesk.Civil.DatabaseServices.Profile";
        private const string CorridorTypeName   = "Autodesk.Civil.DatabaseServices.Corridor";
        private const string TinSurfaceTypeName = "Autodesk.Civil.DatabaseServices.TinSurface";
        private const string PipeTypeName       = "Autodesk.Civil.DatabaseServices.Pipe";
        private const string NetworkTypeName    = "Autodesk.Civil.DatabaseServices.Network";

        // ── Static type-mapping table ──────────────────────────────────────────────

        /// <summary>
        /// Version-aware type mappings.  When a type was renamed or moved between
        /// Civil 3D releases, add entries here.  The adapter walks this list in
        /// priority order (exact year first, then fallback).
        /// </summary>
        private static readonly ApiTypeMapping[] TypeMappings =
        {
            // ── CivilApplication ────────────────────────────────────────────────
            new(2025, "CivilApplication", CivilAppTypeName,
                "Autodesk.Civil.ApplicationServices"),
            new(2026, "CivilApplication", CivilAppTypeName,
                "Autodesk.Civil.ApplicationServices"),
            new(2027, "CivilApplication", CivilAppTypeName,
                "Autodesk.Civil.ApplicationServices"),

            // ── CivilDocument ────────────────────────────────────────────────────
            new(2025, "CivilDocument", CivilDocTypeName,
                "Autodesk.Civil.ApplicationServices"),
            new(2026, "CivilDocument", CivilDocTypeName,
                "Autodesk.Civil.ApplicationServices"),
            new(2027, "CivilDocument", CivilDocTypeName,
                "Autodesk.Civil.ApplicationServices"),

            // ── Alignment ────────────────────────────────────────────────────────
            new(2025, "Alignment", AlignmentTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2026, "Alignment", AlignmentTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2027, "Alignment", AlignmentTypeName,
                "Autodesk.Civil.DatabaseServices"),

            // ── Profile ──────────────────────────────────────────────────────────
            new(2025, "Profile", ProfileTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2026, "Profile", ProfileTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2027, "Profile", ProfileTypeName,
                "Autodesk.Civil.DatabaseServices"),

            // ── Corridor ─────────────────────────────────────────────────────────
            new(2025, "Corridor", CorridorTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2026, "Corridor", CorridorTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2027, "Corridor", CorridorTypeName,
                "Autodesk.Civil.DatabaseServices"),

            // ── TinSurface ───────────────────────────────────────────────────────
            new(2025, "TinSurface", TinSurfaceTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2026, "TinSurface", TinSurfaceTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2027, "TinSurface", TinSurfaceTypeName,
                "Autodesk.Civil.DatabaseServices"),

            // ── Pipe (AeccXUiPipe) ───────────────────────────────────────────────
            new(2025, "Pipe", PipeTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2026, "Pipe", PipeTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2027, "Pipe", PipeTypeName,
                "Autodesk.Civil.DatabaseServices"),

            // ── Network ──────────────────────────────────────────────────────────
            new(2025, "Network", NetworkTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2026, "Network", NetworkTypeName,
                "Autodesk.Civil.DatabaseServices"),
            new(2027, "Network", NetworkTypeName,
                "Autodesk.Civil.DatabaseServices"),

#if CIVIL3D_2027
            // Example: hypothetical API added in 2027.
            new(2027, "SubassemblyComposer",
                "Autodesk.Civil.DatabaseServices.SubassemblyComposer",
                "Autodesk.Civil.DatabaseServices"),
#endif
        };

        // ── Method signature overrides ─────────────────────────────────────────────

        /// <summary>
        /// When a method was renamed or received a different signature in a specific
        /// version, list the override here.
        /// Key: (logicalTypeName, logicalMethodName, versionYear)
        /// Value: actual method name in that version
        /// </summary>
        private static readonly IReadOnlyDictionary<(string, string, int), string> MethodNameOverrides =
            new Dictionary<(string, string, int), string>
            {
#if CIVIL3D_2026
                // Example: GetActiveCivilDocument was renamed in 2026.
                { ("CivilApplication", "GetActiveCivilDocument", 2026),
                  "ActiveCivilDocument" },
#endif
#if CIVIL3D_2027
                // Example: hypothetical 2027 rename.
                { ("CivilApplication", "GetActiveCivilDocument", 2027),
                  "ActiveCivilDocument" },
#endif
            };

        // ── Instance state ─────────────────────────────────────────────────────────

        private readonly Civil3DVersionInfo _versionInfo;
        private readonly ConcurrentDictionary<string, Assembly> _assemblyCache = new();
        private readonly ConcurrentDictionary<string, Type> _typeCache = new();
        private readonly ConcurrentDictionary<string, ResolvedApiMethod> _methodCache = new();
        private int _disposed; // 0 = alive, 1 = disposed (Interlocked)

        // ── Construction ──────────────────────────────────────────────────────────

        /// <summary>
        /// Initialises the adapter for the Civil 3D installation described by
        /// <paramref name="versionInfo"/>.
        /// </summary>
        /// <param name="versionInfo">
        /// Version metadata returned by <see cref="Civil3DVersionDetector"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="versionInfo"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when the version year is outside the supported range.
        /// </exception>
        public ApiVersionAdapter(Civil3DVersionInfo versionInfo)
        {
            _versionInfo = versionInfo
                ?? throw new ArgumentNullException(nameof(versionInfo));

            if (!versionInfo.IsSupported)
                throw new NotSupportedException(
                    $"Civil 3D {versionInfo.VersionYear} is not supported. " +
                    $"Supported range: {Civil3DVersionDetector.MinSupportedYear}" +
                    $"–{Civil3DVersionDetector.MaxSupportedYear}.");
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Gets the Civil 3D version year this adapter targets.</summary>
        public int VersionYear => _versionInfo.VersionYear;

        /// <summary>Gets the underlying version info object.</summary>
        public Civil3DVersionInfo VersionInfo => _versionInfo;

        /// <summary>
        /// Resolves the <see cref="Type"/> identified by its logical name in the
        /// type-mapping table.  Loaded assemblies and resolved types are cached.
        /// </summary>
        /// <param name="logicalTypeName">
        /// The logical (short) type name as registered in <see cref="TypeMappings"/>
        /// (e.g. <c>"CivilApplication"</c>).
        /// </param>
        /// <returns>The resolved <see cref="Type"/>.</returns>
        /// <exception cref="TypeLoadException">
        /// Thrown when the type cannot be resolved for the current version.
        /// </exception>
        public Type ResolveType(string logicalTypeName)
        {
            ThrowIfDisposed();

            string cacheKey = $"{_versionInfo.VersionYear}:{logicalTypeName}";
            if (_typeCache.TryGetValue(cacheKey, out Type? cached))
                return cached;

            ApiTypeMapping mapping = FindTypeMapping(logicalTypeName)
                ?? throw new TypeLoadException(
                    $"No type mapping registered for \"{logicalTypeName}\" " +
                    $"in Civil 3D {_versionInfo.VersionYear}.");

            Assembly asm = LoadAssembly(mapping.AssemblyName);
            Type? type = asm.GetType(mapping.FullTypeName, throwOnError: false);

            if (type is null)
            {
                // Fallback: search all exported types for a name match.
                type = asm.GetExportedTypes()
                    .FirstOrDefault(t =>
                        string.Equals(t.Name, logicalTypeName,
                            StringComparison.OrdinalIgnoreCase));
            }

            if (type is null)
                throw new TypeLoadException(
                    $"Type \"{mapping.FullTypeName}\" was not found in " +
                    $"assembly \"{mapping.AssemblyName}\" for Civil 3D {_versionInfo.VersionYear}.");

            _typeCache.TryAdd(cacheKey, type);
            return type;
        }

        /// <summary>
        /// Resolves a method on the type identified by <paramref name="logicalTypeName"/>.
        /// Method names may be overridden per-version via <see cref="MethodNameOverrides"/>.
        /// </summary>
        /// <param name="logicalTypeName">The logical type name (see <see cref="ResolveType"/>).</param>
        /// <param name="logicalMethodName">The logical method name.</param>
        /// <param name="bindingFlags">
        /// Binding flags.  Defaults to <c>Public | Static | Instance</c>.
        /// </param>
        /// <returns>A <see cref="ResolvedApiMethod"/> ready for invocation.</returns>
        /// <exception cref="MissingMethodException">
        /// Thrown when no matching method is found.
        /// </exception>
        public ResolvedApiMethod ResolveMethod(
            string logicalTypeName,
            string logicalMethodName,
            BindingFlags bindingFlags =
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
        {
            ThrowIfDisposed();

            string cacheKey =
                $"{_versionInfo.VersionYear}:{logicalTypeName}.{logicalMethodName}:{(int)bindingFlags}";

            if (_methodCache.TryGetValue(cacheKey, out ResolvedApiMethod? cached))
                return cached;

            Type type = ResolveType(logicalTypeName);

            // Check for version-specific name overrides.
            string actualMethodName = logicalMethodName;
            if (MethodNameOverrides.TryGetValue(
                    (logicalTypeName, logicalMethodName, _versionInfo.VersionYear),
                    out string? overriddenName))
            {
                actualMethodName = overriddenName;
            }

            MethodInfo? method = type.GetMethod(actualMethodName, bindingFlags);

            if (method is null)
            {
                // Attempt case-insensitive fallback.
                method = type.GetMethods(bindingFlags)
                    .FirstOrDefault(m => string.Equals(
                        m.Name, actualMethodName,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (method is null)
                throw new MissingMethodException(
                    $"Method \"{actualMethodName}\" (logical: \"{logicalMethodName}\") " +
                    $"not found on type \"{type.FullName}\" for Civil 3D {_versionInfo.VersionYear}.");

            var resolved = new ResolvedApiMethod
            {
                Method      = method,
                DeclaringType = type,
                VersionYear = _versionInfo.VersionYear
            };

            _methodCache.TryAdd(cacheKey, resolved);
            return resolved;
        }

        /// <summary>
        /// Resolves a property getter as an invocable <see cref="ResolvedApiMethod"/>.
        /// </summary>
        public ResolvedApiMethod ResolveProperty(
            string logicalTypeName,
            string propertyName,
            BindingFlags bindingFlags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        {
            ThrowIfDisposed();

            Type type = ResolveType(logicalTypeName);

            PropertyInfo? prop = type.GetProperty(propertyName, bindingFlags);
            if (prop is null)
                throw new MissingMemberException(
                    $"Property \"{propertyName}\" not found on \"{type.FullName}\".");

            MethodInfo? getter = prop.GetGetMethod(nonPublic: false);
            if (getter is null)
                throw new MissingMethodException(
                    $"Property \"{propertyName}\" has no public getter on \"{type.FullName}\".");

            return new ResolvedApiMethod
            {
                Method        = getter,
                DeclaringType = type,
                VersionYear   = _versionInfo.VersionYear
            };
        }

        /// <summary>
        /// Loads and returns the assembly with the given logical name from the
        /// install path recorded in <see cref="VersionInfo"/>.
        /// Assemblies are cached; each DLL is loaded only once.
        /// </summary>
        /// <param name="assemblyName">
        /// Short assembly name without extension (e.g.
        /// <c>"Autodesk.Civil.ApplicationServices"</c>).
        /// </param>
        /// <returns>The loaded <see cref="Assembly"/>.</returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the DLL is not present in the install path.
        /// </exception>
        public Assembly LoadAssembly(string assemblyName)
        {
            ThrowIfDisposed();

            if (_assemblyCache.TryGetValue(assemblyName, out Assembly? cached))
                return cached;

            // 1. Check if already loaded in the AppDomain.
            Assembly? asm = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, assemblyName,
                        StringComparison.OrdinalIgnoreCase));

            // 2. Try path recorded by version detector.
            if (asm is null &&
                _versionInfo.ResolvedDlls.TryGetValue(assemblyName, out string? dllPath) &&
                File.Exists(dllPath))
            {
                asm = LoadFromFile(dllPath, assemblyName);
            }

            // 3. Fallback: construct path from install dir.
            if (asm is null)
            {
                string fallback = Path.Combine(
                    _versionInfo.InstallPath, $"{assemblyName}.dll");

                if (File.Exists(fallback))
                    asm = LoadFromFile(fallback, assemblyName);
            }

            if (asm is null)
                throw new FileNotFoundException(
                    $"Assembly \"{assemblyName}\" not found for " +
                    $"Civil 3D {_versionInfo.VersionYear}. " +
                    $"Install path: \"{_versionInfo.InstallPath}\".");

            _assemblyCache.TryAdd(assemblyName, asm);
            return asm;
        }

        /// <summary>
        /// Returns <c>true</c> when the given logical type name is available in the
        /// current version, without throwing.
        /// </summary>
        public bool IsTypeAvailable(string logicalTypeName)
        {
            try
            {
                ResolveType(logicalTypeName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns <c>true</c> when the given logical method name is available on the
        /// specified type for the current version, without throwing.
        /// </summary>
        public bool IsMethodAvailable(string logicalTypeName, string logicalMethodName)
        {
            try
            {
                ResolveMethod(logicalTypeName, logicalMethodName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _assemblyCache.Clear();
            _typeCache.Clear();
            _methodCache.Clear();
            GC.SuppressFinalize(this);
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private ApiTypeMapping? FindTypeMapping(string logicalTypeName)
        {
            // Exact version match first.
            ApiTypeMapping? mapping = TypeMappings.FirstOrDefault(m =>
                m.VersionYear == _versionInfo.VersionYear &&
                string.Equals(m.LogicalName, logicalTypeName,
                    StringComparison.OrdinalIgnoreCase));

            // Fall back to any version's mapping (type name likely unchanged).
            return mapping ?? TypeMappings.FirstOrDefault(m =>
                string.Equals(m.LogicalName, logicalTypeName,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static Assembly LoadFromFile(string path, string assemblyName)
        {
            try
            {
                return Assembly.LoadFrom(path);
            }
            catch (Exception ex)
            {
                throw new FileLoadException(
                    $"Failed to load assembly \"{assemblyName}\" from \"{path}\": {ex.Message}",
                    path, ex);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ApiVersionAdapter));
        }
    }
}
