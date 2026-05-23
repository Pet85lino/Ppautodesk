using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

// Conditional compile guards allow the file to be compiled both inside and
// outside a live Civil 3D process.  When compiling against the real Civil 3D
// DLLs, these symbols are defined by the .csproj.
#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.Civil.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace Autodesk.Civil3D.Connector.Core
{
    // ──────────────────────────────────────────────────────────────────────────────
    // Supporting types
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Severity levels used by the connector logging infrastructure.</summary>
    public enum LogLevel
    {
        /// <summary>Detailed diagnostic information.</summary>
        Debug,
        /// <summary>General informational messages.</summary>
        Info,
        /// <summary>Non-fatal warnings that may indicate a problem.</summary>
        Warning,
        /// <summary>Recoverable errors.</summary>
        Error,
        /// <summary>Unrecoverable errors that require connector shutdown.</summary>
        Fatal
    }

    /// <summary>A single log entry emitted by the connector infrastructure.</summary>
    public sealed class ConnectorLogEntry
    {
        /// <summary>UTC timestamp of the entry.</summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Severity level.</summary>
        public LogLevel Level { get; init; }

        /// <summary>Human-readable message.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>Exception, if any.</summary>
        public Exception? Exception { get; init; }

        /// <summary>Source member name (populated by compiler via CallerMemberName).</summary>
        public string? CallerMemberName { get; init; }

        /// <summary>Source file path (populated by compiler via CallerFilePath).</summary>
        public string? CallerFilePath { get; init; }

        /// <inheritdoc/>
        public override string ToString()
        {
            string file = CallerFilePath is not null
                ? Path.GetFileName(CallerFilePath)
                : "?";

            string prefix = $"[{Timestamp:HH:mm:ss.fff}] [{Level,-7}] [{file}/{CallerMemberName}]";
            return Exception is null
                ? $"{prefix} {Message}"
                : $"{prefix} {Message} | {Exception.GetType().Name}: {Exception.Message}";
        }
    }

    /// <summary>
    /// Delegate invoked whenever the connector emits a log entry.
    /// </summary>
    public delegate void ConnectorLogHandler(ConnectorLogEntry entry);

    /// <summary>
    /// Snapshot of the connector's runtime state, returned by
    /// <see cref="ConnectorBase.GetDiagnostics"/>.
    /// </summary>
    public sealed class ConnectorDiagnostics
    {
        /// <summary>Name of the connector class.</summary>
        public string ConnectorName { get; init; } = string.Empty;

        /// <summary>Detected Civil 3D version.</summary>
        public Civil3DVersionInfo? VersionInfo { get; init; }

        /// <summary>Whether the connector is currently initialised.</summary>
        public bool IsInitialised { get; init; }

        /// <summary>Number of log entries recorded.</summary>
        public int LogEntryCount { get; init; }

        /// <summary>UTC time when the connector was created.</summary>
        public DateTimeOffset CreatedAt { get; init; }

        /// <summary>Active (non-completed) transaction count.</summary>
        public int ActiveTransactionCount { get; init; }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // ConnectorBase
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Abstract base class for all Civil 3D connector implementations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Responsibilities:
    /// <list type="bullet">
    ///   <item>Detect and hold the Civil 3D version via <see cref="Civil3DVersionDetector"/>.</item>
    ///   <item>Create and expose the per-version <see cref="ApiVersionAdapter"/>.</item>
    ///   <item>Provide a structured logging pipeline with subscriber support.</item>
    ///   <item>Expose the AutoCAD document/database handle (when running in-process).</item>
    ///   <item>Manage a <see cref="TransactionManager"/> for database operations.</item>
    ///   <item>Implement <see cref="IDisposable"/> with full cleanup.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Derived classes must implement <see cref="OnInitialiseAsync"/> and
    /// <see cref="OnDisposeAsync"/> for lifecycle management.
    /// </para>
    /// </remarks>
    public abstract class ConnectorBase : IDisposable
    {
        // ── Fields ────────────────────────────────────────────────────────────────

        private readonly object _initLock = new();
        private volatile bool _initialised;
        private int _disposed; // 0 = alive; 1 = disposed (Interlocked)

        private readonly ConcurrentQueue<ConnectorLogEntry> _logQueue
            = new();

        private readonly List<ConnectorLogHandler> _logSubscribers
            = new();

        private readonly ReaderWriterLockSlim _subscriberLock
            = new(LockRecursionPolicy.SupportsRecursion);

        private Civil3DVersionDetector? _detector;
        private ApiVersionAdapter?      _adapter;
        private TransactionManager?     _transactionManager;

        private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow;

        // ── Construction ──────────────────────────────────────────────────────────

        /// <summary>
        /// Initialises the base connector.
        /// </summary>
        /// <param name="connectorName">
        /// A human-readable name for this connector (used in diagnostics/logs).
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="connectorName"/> is <c>null</c>.
        /// </exception>
        protected ConnectorBase(string connectorName)
        {
            ConnectorName = connectorName
                ?? throw new ArgumentNullException(nameof(connectorName));
        }

        // ── Properties ────────────────────────────────────────────────────────────

        /// <summary>Gets the human-readable name of this connector.</summary>
        public string ConnectorName { get; }

        /// <summary>
        /// Gets the detected Civil 3D version info, available after
        /// <see cref="Initialise"/> completes.
        /// </summary>
        public Civil3DVersionInfo? VersionInfo { get; private set; }

        /// <summary>
        /// Gets the version adapter, available after <see cref="Initialise"/> completes.
        /// </summary>
        public ApiVersionAdapter? Adapter => _adapter;

        /// <summary>
        /// Gets the transaction manager, available after <see cref="Initialise"/> completes.
        /// </summary>
        public TransactionManager? Transactions => _transactionManager;

        /// <summary>Gets whether the connector has been successfully initialised.</summary>
        public bool IsInitialised => _initialised;

        // ──────────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Detects the Civil 3D version, creates the adapter and transaction manager,
        /// then calls <see cref="OnInitialiseAsync"/>.
        /// Thread-safe; subsequent calls are no-ops.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no supported Civil 3D installation is found.
        /// </exception>
        public void Initialise()
        {
            if (_initialised) return;

            lock (_initLock)
            {
                if (_initialised) return;

                ThrowIfDisposed();

                try
                {
                    LogInfo("Starting connector initialisation.");

                    // 1. Detect version.
                    _detector = new Civil3DVersionDetector();
                    Civil3DVersionInfo? info = _detector.DetectBest();

                    if (info is null)
                        throw new InvalidOperationException(
                            "No supported Civil 3D installation (2025–2027) was found on this machine.");

                    VersionInfo = info;
                    LogInfo(
                        $"Detected {VersionInfo}.");

                    // 2. Create adapter.
                    _adapter = new ApiVersionAdapter(VersionInfo);
                    LogInfo(
                        $"ApiVersionAdapter created for year {VersionInfo.VersionYear}.");

                    // 3. Create transaction manager.
                    _transactionManager = CreateTransactionManager();
                    LogInfo("TransactionManager created.");

                    // 4. Delegate to derived class.
                    OnInitialise();

                    _initialised = true;
                    LogInfo("Connector initialisation complete.");
                }
                catch (Exception ex)
                {
                    LogError("Connector initialisation failed.", ex);
                    throw;
                }
            }
        }

        /// <summary>
        /// Override in derived classes to perform version-specific initialisation
        /// after the base infrastructure is ready.
        /// </summary>
        protected virtual void OnInitialise() { }

        /// <summary>
        /// Creates the <see cref="TransactionManager"/> to use for this connector.
        /// Override to supply a custom subclass.
        /// </summary>
        protected virtual TransactionManager CreateTransactionManager()
        {
#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            return new TransactionManager(doc?.Database, this);
#else
            return new TransactionManager(null, this);
#endif
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Document helpers
        // ──────────────────────────────────────────────────────────────────────────

#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027

        /// <summary>
        /// Gets the currently active AutoCAD <see cref="Document"/>.
        /// Only available when running inside Civil 3D.
        /// </summary>
        public Document? ActiveDocument
        {
            get
            {
                ThrowIfNotInitialised();
                try
                {
                    return AcApp.DocumentManager.MdiActiveDocument;
                }
                catch (Exception ex)
                {
                    LogError("Failed to retrieve active AutoCAD document.", ex);
                    return null;
                }
            }
        }

        /// <summary>
        /// Gets the active AutoCAD <see cref="Database"/>.
        /// </summary>
        public Database? ActiveDatabase => ActiveDocument?.Database;

        /// <summary>
        /// Gets the active Civil 3D document.
        /// </summary>
        public CivilDocument? ActiveCivilDocument
        {
            get
            {
                ThrowIfNotInitialised();
                try
                {
                    Document? acDoc = AcApp.DocumentManager.MdiActiveDocument;
                    return acDoc is null
                        ? null
                        : CivilApplication.ActiveDocument;
                }
                catch (Exception ex)
                {
                    LogError("Failed to retrieve active Civil 3D document.", ex);
                    return null;
                }
            }
        }

        /// <summary>
        /// Retrieves the AutoCAD command-line editor.
        /// </summary>
        public Editor? Editor
        {
            get
            {
                try
                {
                    return AcApp.DocumentManager.MdiActiveDocument?.Editor;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Writes a message to the AutoCAD command line, if available.
        /// </summary>
        /// <param name="message">Message text (newline appended if absent).</param>
        public void WriteToCommandLine(string message)
        {
            try
            {
                string text = message.EndsWith('\n') ? message : message + "\n";
                Editor?.WriteMessage(text);
            }
            catch (Exception ex)
            {
                LogWarning($"WriteToCommandLine failed: {ex.Message}");
            }
        }

#endif // CIVIL3D_*

        // ──────────────────────────────────────────────────────────────────────────
        // Logging
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Subscribes a handler to receive all future log entries.
        /// </summary>
        public void AddLogSubscriber(ConnectorLogHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _subscriberLock.EnterWriteLock();
            try { _logSubscribers.Add(handler); }
            finally { _subscriberLock.ExitWriteLock(); }
        }

        /// <summary>Removes a previously registered log subscriber.</summary>
        public void RemoveLogSubscriber(ConnectorLogHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _subscriberLock.EnterWriteLock();
            try { _logSubscribers.Remove(handler); }
            finally { _subscriberLock.ExitWriteLock(); }
        }

        /// <summary>Returns a snapshot of all log entries recorded so far.</summary>
        public IReadOnlyList<ConnectorLogEntry> GetLogSnapshot()
            => _logQueue.ToArray();

        /// <summary>
        /// Returns a <see cref="ConnectorDiagnostics"/> snapshot of the connector state.
        /// </summary>
        public ConnectorDiagnostics GetDiagnostics() => new()
        {
            ConnectorName        = ConnectorName,
            VersionInfo          = VersionInfo,
            IsInitialised        = _initialised,
            LogEntryCount        = _logQueue.Count,
            CreatedAt            = _createdAt,
            ActiveTransactionCount = _transactionManager?.ActiveTransactionCount ?? 0
        };

        // ── Protected logging helpers ──────────────────────────────────────────────

        /// <summary>Logs a debug-level message.</summary>
        protected void LogDebug(
            string message,
            [CallerMemberName] string? callerName = null,
            [CallerFilePath]   string? callerFile = null)
            => Emit(LogLevel.Debug, message, null, callerName, callerFile);

        /// <summary>Logs an informational message.</summary>
        protected void LogInfo(
            string message,
            [CallerMemberName] string? callerName = null,
            [CallerFilePath]   string? callerFile = null)
            => Emit(LogLevel.Info, message, null, callerName, callerFile);

        /// <summary>Logs a warning.</summary>
        protected void LogWarning(
            string message,
            Exception? ex = null,
            [CallerMemberName] string? callerName = null,
            [CallerFilePath]   string? callerFile = null)
            => Emit(LogLevel.Warning, message, ex, callerName, callerFile);

        /// <summary>Logs a recoverable error.</summary>
        protected void LogError(
            string message,
            Exception? ex = null,
            [CallerMemberName] string? callerName = null,
            [CallerFilePath]   string? callerFile = null)
            => Emit(LogLevel.Error, message, ex, callerName, callerFile);

        /// <summary>Logs a fatal error.</summary>
        protected void LogFatal(
            string message,
            Exception? ex = null,
            [CallerMemberName] string? callerName = null,
            [CallerFilePath]   string? callerFile = null)
            => Emit(LogLevel.Fatal, message, ex, callerName, callerFile);

        // ── Internal log emission ──────────────────────────────────────────────────

        private void Emit(
            LogLevel level,
            string message,
            Exception? ex,
            string? callerName,
            string? callerFile)
        {
            var entry = new ConnectorLogEntry
            {
                Level            = level,
                Message          = message,
                Exception        = ex,
                CallerMemberName = callerName,
                CallerFilePath   = callerFile
            };

            _logQueue.Enqueue(entry);

            // Dispatch to subscribers (read lock – multiple readers allowed).
            _subscriberLock.EnterReadLock();
            try
            {
                foreach (ConnectorLogHandler handler in _logSubscribers)
                {
                    try { handler(entry); }
                    catch { /* subscriber exceptions must not propagate */ }
                }
            }
            finally
            {
                _subscriberLock.ExitReadLock();
            }

            // Also write to Debug output for IDE integration.
            Debug.WriteLine(entry.ToString());
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Guard helpers
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> when the connector has been disposed.
        /// </summary>
        protected void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(ConnectorName);
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> when
        /// <see cref="Initialise"/> has not yet been called.
        /// </summary>
        protected void ThrowIfNotInitialised()
        {
            ThrowIfDisposed();
            if (!_initialised)
                throw new InvalidOperationException(
                    $"Connector \"{ConnectorName}\" has not been initialised. " +
                    "Call Initialise() first.");
        }

        // ──────────────────────────────────────────────────────────────────────────
        // IDisposable
        // ──────────────────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                LogInfo("Connector is being disposed.");
                OnDispose();
            }
            catch (Exception ex)
            {
                LogError("Error during connector disposal.", ex);
            }
            finally
            {
                _transactionManager?.Dispose();
                _adapter?.Dispose();
                _detector?.Dispose();

                _subscriberLock.EnterWriteLock();
                try { _logSubscribers.Clear(); }
                finally { _subscriberLock.ExitWriteLock(); }
                _subscriberLock.Dispose();

                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Override in derived classes to release version-specific resources
        /// before the base class cleans up.
        /// </summary>
        protected virtual void OnDispose() { }
    }
}
