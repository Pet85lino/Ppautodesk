using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
using Autodesk.AutoCAD.DatabaseServices;
using AcTransaction = Autodesk.AutoCAD.DatabaseServices.Transaction;
using AcTransactionManager = Autodesk.AutoCAD.DatabaseServices.TransactionManager;
#endif

namespace Autodesk.Civil3D.Connector.Core
{
    // ──────────────────────────────────────────────────────────────────────────────
    // Supporting types
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Current state of a <see cref="ManagedTransaction"/>.</summary>
    public enum TransactionState
    {
        /// <summary>Transaction is open and accepting changes.</summary>
        Open,
        /// <summary>Transaction has been committed successfully.</summary>
        Committed,
        /// <summary>Transaction has been rolled back.</summary>
        RolledBack,
        /// <summary>Transaction is in an error state.</summary>
        Error
    }

    /// <summary>Options that control the behaviour of a managed transaction.</summary>
    public sealed class TransactionOptions
    {
        /// <summary>
        /// Identifier for diagnostic purposes. If empty a GUID is generated.
        /// </summary>
        public string Name { get; init; } = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// When <c>true</c>, a <see cref="ManagedTransaction"/> that is disposed
        /// without an explicit call to <see cref="ManagedTransaction.Commit"/> will
        /// be automatically rolled back.  Default: <c>true</c>.
        /// </summary>
        public bool RollbackOnDispose { get; init; } = true;

        /// <summary>
        /// When <c>true</c>, the manager logs a warning if the transaction is
        /// disposed without being committed. Default: <c>true</c>.
        /// </summary>
        public bool WarnOnImplicitRollback { get; init; } = true;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // ManagedTransaction
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps an AutoCAD <c>Transaction</c> (or a nested sub-transaction) with
    /// structured commit/rollback semantics and automatic resource cleanup.
    /// </summary>
    public sealed class ManagedTransaction : IDisposable
    {
        // ── Fields ────────────────────────────────────────────────────────────────

        private readonly TransactionManager _owner;
        private readonly TransactionOptions _options;
        private volatile TransactionState   _state = TransactionState.Open;
        private int _disposed;

#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
        private readonly AcTransaction _acTransaction;
#endif

        // ── Construction ──────────────────────────────────────────────────────────

#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
        internal ManagedTransaction(
            AcTransaction acTransaction,
            TransactionManager owner,
            TransactionOptions options,
            int depth)
        {
            _acTransaction = acTransaction
                ?? throw new ArgumentNullException(nameof(acTransaction));
            _owner   = owner   ?? throw new ArgumentNullException(nameof(owner));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            Depth    = depth;
        }
#else
        internal ManagedTransaction(
            TransactionManager owner,
            TransactionOptions options,
            int depth)
        {
            _owner   = owner   ?? throw new ArgumentNullException(nameof(owner));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            Depth    = depth;
        }
#endif

        // ── Properties ────────────────────────────────────────────────────────────

        /// <summary>Gets the display name assigned to this transaction.</summary>
        public string Name => _options.Name;

        /// <summary>Gets the nesting depth (0 = top-level).</summary>
        public int Depth { get; }

        /// <summary>Gets the current state of this transaction.</summary>
        public TransactionState State => _state;

        /// <summary>Gets whether this transaction is still open.</summary>
        public bool IsOpen => _state == TransactionState.Open;

#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
        /// <summary>
        /// Gets the underlying AutoCAD <see cref="AcTransaction"/>.
        /// Available only when compiled against Civil 3D DLLs.
        /// </summary>
        public AcTransaction AcTransaction => _acTransaction;

        /// <summary>
        /// Opens a <see cref="DBObject"/> within this transaction for the given
        /// <see cref="OpenMode"/>.
        /// </summary>
        /// <typeparam name="T">Expected DBObject subtype.</typeparam>
        /// <param name="objectId">The ObjectId to open.</param>
        /// <param name="mode">Read or Write.</param>
        /// <param name="forceOpenOnLockedLayer">
        /// When <c>true</c>, opens even if the object is on a locked layer.
        /// </param>
        /// <returns>The opened <typeparamref name="T"/> instance.</returns>
        public T GetObject<T>(
            ObjectId objectId,
            OpenMode mode = OpenMode.ForRead,
            bool forceOpenOnLockedLayer = false)
            where T : DBObject
        {
            ThrowIfNotOpen();
            try
            {
                return (T)_acTransaction.GetObject(
                    objectId, mode, openErased: false,
                    forceOpenOnLockedLayer: forceOpenOnLockedLayer);
            }
            catch (Exception ex)
            {
                _state = TransactionState.Error;
                throw new InvalidOperationException(
                    $"GetObject<{typeof(T).Name}> failed for ObjectId {objectId}: {ex.Message}",
                    ex);
            }
        }
#endif

        // ── Commit / Rollback ─────────────────────────────────────────────────────

        /// <summary>
        /// Commits all changes made within this transaction to the database.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the transaction is not in the <see cref="TransactionState.Open"/> state.
        /// </exception>
        public void Commit()
        {
            ThrowIfNotOpen();
            ThrowIfDisposed();

            try
            {
#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
                _acTransaction.Commit();
#endif
                _state = TransactionState.Committed;
                _owner.OnTransactionCommitted(this);
            }
            catch (Exception ex)
            {
                _state = TransactionState.Error;
                _owner.OnTransactionError(this, ex);
                throw;
            }
        }

        /// <summary>
        /// Rolls back all changes made within this transaction.
        /// </summary>
        public void Rollback()
        {
            if (_state != TransactionState.Open) return;

            try
            {
#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
                _acTransaction.Abort();
#endif
                _state = TransactionState.RolledBack;
                _owner.OnTransactionRolledBack(this);
            }
            catch (Exception ex)
            {
                _state = TransactionState.Error;
                _owner.OnTransactionError(this, ex);
                throw;
            }
        }

        // ── IDisposable ───────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                if (_state == TransactionState.Open)
                {
                    if (_options.RollbackOnDispose)
                    {
                        if (_options.WarnOnImplicitRollback)
                        {
                            _owner.LogWarning(
                                $"Transaction \"{Name}\" (depth {Depth}) " +
                                "disposed without explicit Commit – rolling back.");
                        }
                        Rollback();
                    }
                }
            }
            finally
            {
#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
                try { _acTransaction.Dispose(); } catch { /* best-effort */ }
#endif
                _owner.OnTransactionDisposed(this);
                GC.SuppressFinalize(this);
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private void ThrowIfNotOpen()
        {
            if (_state != TransactionState.Open)
                throw new InvalidOperationException(
                    $"Transaction \"{Name}\" is not open (state: {_state}).");
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ManagedTransaction));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // TransactionManager
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Manages the creation and lifecycle of Civil 3D / AutoCAD transactions.
    /// Supports nested (sub-)transactions, rollback, commit, and error recovery.
    /// Thread-safe for concurrent reads; write operations are serialised with a lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Typical usage:
    /// <code>
    ///   using var tx = connector.Transactions!.Begin(new TransactionOptions { Name = "AddAlignment" });
    ///   // ... modify the database ...
    ///   tx.Commit();
    /// </code>
    /// </para>
    /// <para>
    /// Nested transactions are supported.  The inner transaction must be committed
    /// or rolled back before the outer one can be committed.
    /// </para>
    /// </remarks>
    public sealed class TransactionManager : IDisposable
    {
        // ── Fields ────────────────────────────────────────────────────────────────

        private readonly object _lock = new();
        private readonly Stack<ManagedTransaction> _stack = new();
        private readonly ConnectorBase _owner;
        private int _disposed;

        // Counters for diagnostics.
        private long _totalStarted;
        private long _totalCommitted;
        private long _totalRolledBack;
        private long _totalErrors;

#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
        private readonly Database? _database;
        private AcTransactionManager? AcTm => _database?.TransactionManager;
#endif

        // ── Construction ──────────────────────────────────────────────────────────

#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
        /// <summary>
        /// Creates a transaction manager bound to the specified <paramref name="database"/>.
        /// </summary>
        /// <param name="database">
        /// The AutoCAD <see cref="Database"/> to operate on.
        /// Can be <c>null</c> when running outside of Civil 3D (e.g. unit tests).
        /// </param>
        /// <param name="owner">The owning <see cref="ConnectorBase"/>.</param>
        public TransactionManager(Database? database, ConnectorBase owner)
        {
            _database = database;
            _owner    = owner ?? throw new ArgumentNullException(nameof(owner));
        }
#else
        /// <summary>
        /// Creates a transaction manager without a live AutoCAD database.
        /// Used in headless / test contexts.
        /// </summary>
        public TransactionManager(object? _, ConnectorBase owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }
#endif

        // ── Properties ────────────────────────────────────────────────────────────

        /// <summary>Gets the number of currently open (non-disposed) transactions.</summary>
        public int ActiveTransactionCount
        {
            get { lock (_lock) { return _stack.Count; } }
        }

        /// <summary>Gets the total number of transactions started since creation.</summary>
        public long TotalStarted => Interlocked.Read(ref _totalStarted);

        /// <summary>Gets the total number of transactions committed since creation.</summary>
        public long TotalCommitted => Interlocked.Read(ref _totalCommitted);

        /// <summary>Gets the total number of transactions rolled back since creation.</summary>
        public long TotalRolledBack => Interlocked.Read(ref _totalRolledBack);

        /// <summary>Gets the total number of transactions that entered the Error state.</summary>
        public long TotalErrors => Interlocked.Read(ref _totalErrors);

        /// <summary>
        /// Gets the currently active top-level transaction, or <c>null</c> when the
        /// stack is empty.
        /// </summary>
        public ManagedTransaction? Current
        {
            get
            {
                lock (_lock)
                {
                    return _stack.Count > 0 ? _stack.Peek() : null;
                }
            }
        }

        // ── Core operations ───────────────────────────────────────────────────────

        /// <summary>
        /// Starts a new transaction (or sub-transaction if one is already open)
        /// and returns a <see cref="ManagedTransaction"/> that wraps it.
        /// </summary>
        /// <param name="options">
        /// Options for this transaction.  If <c>null</c>, defaults are used.
        /// </param>
        /// <returns>
        /// An open <see cref="ManagedTransaction"/>.  The caller must dispose it.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no AutoCAD database is available.
        /// </exception>
        public ManagedTransaction Begin(TransactionOptions? options = null)
        {
            ThrowIfDisposed();
            options ??= new TransactionOptions();

            lock (_lock)
            {
                int depth = _stack.Count;
                ManagedTransaction tx = CreateManagedTransaction(options, depth);
                _stack.Push(tx);
                Interlocked.Increment(ref _totalStarted);

                LogInfo(
                    $"Transaction \"{options.Name}\" started " +
                    $"(depth {depth}, total started: {TotalStarted}).");

                return tx;
            }
        }

        /// <summary>
        /// Executes <paramref name="action"/> within a transaction that is
        /// automatically committed on success or rolled back on failure.
        /// </summary>
        /// <param name="action">The work to perform.</param>
        /// <param name="options">Optional transaction options.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="action"/> is <c>null</c>.
        /// </exception>
        public void Execute(
            Action<ManagedTransaction> action,
            TransactionOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(action);
            ThrowIfDisposed();

            options ??= new TransactionOptions { WarnOnImplicitRollback = false };

            using ManagedTransaction tx = Begin(options);
            try
            {
                action(tx);
                if (tx.IsOpen) tx.Commit();
            }
            catch (Exception ex)
            {
                LogError(
                    $"Transaction \"{tx.Name}\" execute block threw – rolling back.", ex);

                if (tx.IsOpen)
                {
                    try { tx.Rollback(); }
                    catch (Exception rbEx)
                    {
                        LogError(
                            $"Rollback of transaction \"{tx.Name}\" also failed.", rbEx);
                    }
                }
                throw;
            }
        }

        /// <summary>
        /// Executes <paramref name="func"/> within a transaction and returns its result.
        /// The transaction is committed on success or rolled back on failure.
        /// </summary>
        /// <typeparam name="TResult">Return type of <paramref name="func"/>.</typeparam>
        /// <param name="func">The work to perform.</param>
        /// <param name="options">Optional transaction options.</param>
        /// <returns>The value returned by <paramref name="func"/>.</returns>
        public TResult Execute<TResult>(
            Func<ManagedTransaction, TResult> func,
            TransactionOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(func);
            ThrowIfDisposed();

            options ??= new TransactionOptions { WarnOnImplicitRollback = false };

            using ManagedTransaction tx = Begin(options);
            try
            {
                TResult result = func(tx);
                if (tx.IsOpen) tx.Commit();
                return result;
            }
            catch (Exception ex)
            {
                LogError(
                    $"Transaction \"{tx.Name}\" execute block threw – rolling back.", ex);

                if (tx.IsOpen)
                {
                    try { tx.Rollback(); }
                    catch (Exception rbEx)
                    {
                        LogError(
                            $"Rollback of transaction \"{tx.Name}\" also failed.", rbEx);
                    }
                }
                throw;
            }
        }

        /// <summary>
        /// Rolls back all currently open transactions in LIFO order.
        /// Used for emergency cleanup.
        /// </summary>
        public void RollbackAll()
        {
            ThrowIfDisposed();
            lock (_lock)
            {
                int count = _stack.Count;
                if (count == 0) return;

                LogWarning($"Rolling back all {count} open transaction(s).");

                while (_stack.Count > 0)
                {
                    ManagedTransaction tx = _stack.Pop();
                    try
                    {
                        if (tx.IsOpen) tx.Rollback();
                    }
                    catch (Exception ex)
                    {
                        LogError(
                            $"Failed to roll back transaction \"{tx.Name}\".", ex);
                    }
                    finally
                    {
                        try { tx.Dispose(); } catch { /* best-effort */ }
                    }
                }
            }
        }

        // ── Internal callbacks (called by ManagedTransaction) ─────────────────────

        internal void OnTransactionCommitted(ManagedTransaction tx)
        {
            Interlocked.Increment(ref _totalCommitted);
            lock (_lock) { _stack.TryPop(out _); }
            LogInfo(
                $"Transaction \"{tx.Name}\" committed " +
                $"(total committed: {TotalCommitted}).");
        }

        internal void OnTransactionRolledBack(ManagedTransaction tx)
        {
            Interlocked.Increment(ref _totalRolledBack);
            lock (_lock) { _stack.TryPop(out _); }
            LogInfo(
                $"Transaction \"{tx.Name}\" rolled back " +
                $"(total rolled back: {TotalRolledBack}).");
        }

        internal void OnTransactionError(ManagedTransaction tx, Exception ex)
        {
            Interlocked.Increment(ref _totalErrors);
            LogError(
                $"Transaction \"{tx.Name}\" encountered an error " +
                $"(total errors: {TotalErrors}).", ex);
        }

        internal void OnTransactionDisposed(ManagedTransaction tx)
        {
            // Final clean-up: remove from stack if still present (shouldn't be
            // after commit/rollback, but guard against abnormal paths).
            lock (_lock)
            {
                if (_stack.Count > 0 && ReferenceEquals(_stack.Peek(), tx))
                    _stack.Pop();
            }
        }

        // ── Logging forwarded from ManagedTransaction ──────────────────────────────

        internal void LogWarning(string message, Exception? ex = null)
            => _owner.GetType()
               .GetMethod("LogWarning",
                   System.Reflection.BindingFlags.Instance |
                   System.Reflection.BindingFlags.NonPublic)?
               .Invoke(_owner, new object?[] { message, ex, null, null });

        internal void LogError(string message, Exception? ex = null)
            => _owner.GetType()
               .GetMethod("LogError",
                   System.Reflection.BindingFlags.Instance |
                   System.Reflection.BindingFlags.NonPublic)?
               .Invoke(_owner, new object?[] { message, ex, null, null });

        internal void LogInfo(string message)
            => _owner.GetType()
               .GetMethod("LogInfo",
                   System.Reflection.BindingFlags.Instance |
                   System.Reflection.BindingFlags.NonPublic)?
               .Invoke(_owner, new object?[] { message, null, null });

        // ── Factory ───────────────────────────────────────────────────────────────

        private ManagedTransaction CreateManagedTransaction(
            TransactionOptions options, int depth)
        {
#if CIVIL3D_2025 || CIVIL3D_2026 || CIVIL3D_2027
            if (AcTm is null)
                throw new InvalidOperationException(
                    "No active AutoCAD database is available. " +
                    "Ensure a document is open before starting a transaction.");

            AcTransaction acTx = depth == 0
                ? AcTm.StartTransaction()
                : AcTm.StartOpenCloseTransaction();

            return new ManagedTransaction(acTx, this, options, depth);
#else
            // Headless / test mode – no real AutoCAD transaction.
            return new ManagedTransaction(this, options, depth);
#endif
        }

        // ── IDisposable ───────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                RollbackAll();
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(TransactionManager));
        }
    }
}
