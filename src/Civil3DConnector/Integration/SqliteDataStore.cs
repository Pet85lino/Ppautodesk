using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Civil3DConnector.Models;

namespace Civil3DConnector.Integration
{
    /// <summary>
    /// Persists analysis results, validation violations, and workflow history
    /// in a local SQLite database alongside the DWG file.
    /// </summary>
    public class SqliteDataStore : IDisposable
    {
        private readonly string _dbPath;
        private SqliteConnection? _connection;
        private bool _disposed;

        public SqliteDataStore(string drawingPath)
        {
            _dbPath = Path.ChangeExtension(drawingPath, ".civil3d.db");
            Initialize();
        }

        private void Initialize()
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS AnalysisRuns (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunDate     TEXT NOT NULL,
                    DrawingPath TEXT NOT NULL,
                    Version     TEXT,
                    TotalScanned INTEGER,
                    Criticals   INTEGER,
                    Warnings    INTEGER
                );

                CREATE TABLE IF NOT EXISTS Issues (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunId       INTEGER REFERENCES AnalysisRuns(Id),
                    Severity    TEXT NOT NULL,
                    Category    TEXT,
                    ObjectName  TEXT,
                    ObjectType  TEXT,
                    Description TEXT,
                    FixSuggestion TEXT
                );

                CREATE TABLE IF NOT EXISTS StandardsViolations (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunDate     TEXT NOT NULL,
                    DrawingPath TEXT NOT NULL,
                    Standard    TEXT NOT NULL,
                    ObjectName  TEXT,
                    Message     TEXT,
                    ActualValue REAL,
                    LimitValue  REAL,
                    Unit        TEXT,
                    Severity    TEXT
                );

                CREATE TABLE IF NOT EXISTS WorkflowHistory (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunDate     TEXT NOT NULL,
                    Command     TEXT,
                    Intent      TEXT,
                    Confidence  REAL,
                    Steps       INTEGER,
                    Success     INTEGER
                );

                CREATE TABLE IF NOT EXISTS GeneratedScripts (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    GeneratedAt TEXT NOT NULL,
                    ScriptType  TEXT NOT NULL,
                    WorkflowName TEXT,
                    FilePath    TEXT,
                    ContentHash TEXT
                );
            ";
            cmd.ExecuteNonQuery();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Analysis Persistence
        // ─────────────────────────────────────────────────────────────────────

        public long SaveAnalysisResult(AnalysisResult result)
        {
            EnsureOpen();
            using var tr = _connection!.BeginTransaction();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO AnalysisRuns (RunDate, DrawingPath, Version, TotalScanned, Criticals, Warnings)
                    VALUES (@date, @path, @ver, @total, @crit, @warn);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@date", result.AnalysisDate.ToString("O"));
                cmd.Parameters.AddWithValue("@path", result.DrawingPath);
                cmd.Parameters.AddWithValue("@ver", result.CivilVersion);
                cmd.Parameters.AddWithValue("@total", result.TotalObjectsScanned);
                cmd.Parameters.AddWithValue("@crit", result.CriticalIssues.Count);
                cmd.Parameters.AddWithValue("@warn", result.Warnings.Count);

                long runId = (long)cmd.ExecuteScalar()!;

                foreach (var issue in result.CriticalIssues)
                    InsertIssue(runId, issue, "Critical");
                foreach (var issue in result.Warnings)
                    InsertIssue(runId, issue, "Warning");
                foreach (var issue in result.InfoItems)
                    InsertIssue(runId, issue, "Info");

                tr.Commit();
                return runId;
            }
            catch
            {
                tr.Rollback();
                throw;
            }
        }

        private void InsertIssue(long runId, ObjectIssue issue, string severity)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Issues (RunId, Severity, Category, ObjectName, ObjectType, Description, FixSuggestion)
                VALUES (@runId, @sev, @cat, @name, @type, @desc, @fix);";
            cmd.Parameters.AddWithValue("@runId", runId);
            cmd.Parameters.AddWithValue("@sev", severity);
            cmd.Parameters.AddWithValue("@cat", issue.Category);
            cmd.Parameters.AddWithValue("@name", issue.ObjectName);
            cmd.Parameters.AddWithValue("@type", issue.ObjectType);
            cmd.Parameters.AddWithValue("@desc", issue.Description);
            cmd.Parameters.AddWithValue("@fix", issue.FixSuggestion ?? "");
            cmd.ExecuteNonQuery();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Standards Violations
        // ─────────────────────────────────────────────────────────────────────

        public void SaveViolations(List<StandardsViolation> violations, string drawingPath)
        {
            EnsureOpen();
            using var tr = _connection!.BeginTransaction();
            try
            {
                foreach (var v in violations)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO StandardsViolations
                            (RunDate, DrawingPath, Standard, ObjectName, Message, ActualValue, LimitValue, Unit, Severity)
                        VALUES (@date, @path, @std, @name, @msg, @actual, @limit, @unit, @sev);";
                    cmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("O"));
                    cmd.Parameters.AddWithValue("@path", drawingPath);
                    cmd.Parameters.AddWithValue("@std", v.Standard);
                    cmd.Parameters.AddWithValue("@name", v.ObjectName);
                    cmd.Parameters.AddWithValue("@msg", v.ViolationMessage);
                    cmd.Parameters.AddWithValue("@actual", v.ActualValue);
                    cmd.Parameters.AddWithValue("@limit", v.LimitValue);
                    cmd.Parameters.AddWithValue("@unit", v.Unit);
                    cmd.Parameters.AddWithValue("@sev", v.Severity.ToString());
                    cmd.ExecuteNonQuery();
                }
                tr.Commit();
            }
            catch
            {
                tr.Rollback();
                throw;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Workflow History
        // ─────────────────────────────────────────────────────────────────────

        public void SaveWorkflowExecution(ExecutionPlan plan, bool success)
        {
            EnsureOpen();
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO WorkflowHistory (RunDate, Command, Intent, Confidence, Steps, Success)
                VALUES (@date, @cmd, @intent, @conf, @steps, @ok);";
            cmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("@cmd", plan.OriginalCommand);
            cmd.Parameters.AddWithValue("@intent", plan.Intent.ToString());
            cmd.Parameters.AddWithValue("@conf", plan.Confidence);
            cmd.Parameters.AddWithValue("@steps", plan.Steps.Count);
            cmd.Parameters.AddWithValue("@ok", success ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Query
        // ─────────────────────────────────────────────────────────────────────

        public List<string> GetLastAnalysisSummary()
        {
            EnsureOpen();
            var result = new List<string>();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                SELECT ar.RunDate, ar.Criticals, ar.Warnings, ar.TotalScanned
                FROM AnalysisRuns ar
                ORDER BY ar.Id DESC LIMIT 1;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add($"Last run: {reader.GetString(0)} | " +
                           $"Scanned: {reader.GetInt32(3)} | " +
                           $"Criticals: {reader.GetInt32(1)} | " +
                           $"Warnings: {reader.GetInt32(2)}");

            return result;
        }

        private void EnsureOpen()
        {
            if (_connection?.State != System.Data.ConnectionState.Open)
                _connection?.Open();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connection?.Close();
                _connection?.Dispose();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
