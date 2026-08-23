using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;

namespace PortProSage.Core.Data;

/// <summary>
/// Tracks, all scoped per Sage 50 company file (see CurrentSage50Path):
///   1. The "last changed date" watermark used for the automatic polling sync.
///   2. Which PortPro invoice ids have already been imported into Sage 50, so a
///      re-fetched or re-triggered invoice is never double-booked.
///   3. Which PortPro customers have been synced/refreshed, and when.
///
/// Confirmed live 2026-08-24 as a real, repeat-tripped problem: this state used
/// to be one single, unscoped pool regardless of which Sage 50 company file
/// Sage50Settings.CompanyDataPath pointed at - switching from a DEV company file
/// to PROD (or back) left every invoice/customer from DEV permanently marked as
/// already-imported/already-synced against PROD too, even though PROD had never
/// actually seen any of them (ImportedInvoiceStateService's "Clear All Imported-
/// Invoice Records" button on the Settings tab was the manual workaround for
/// exactly this). Every table now carries a sage50_path column as part of its
/// key, so switching company files is a genuinely fresh start for that file,
/// while switching back to a previously-used file picks its own history back up
/// exactly where it left off.
/// </summary>
public class SyncStateRepository
{
    private readonly string _connectionString;
    private readonly Sage50Settings _sage50Settings;
    private readonly ILogger<SyncStateRepository> _logger;

    public SyncStateRepository(SyncSettings settings, Sage50Settings sage50Settings, ILogger<SyncStateRepository> logger)
    {
        var dir = Path.GetDirectoryName(settings.StateDatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={settings.StateDatabasePath}";
        _sage50Settings = sage50Settings;
        _logger = logger;
        Initialize();
    }

    /// <summary>The scoping key for every table in this file - Sage50Settings.
    /// CompanyDataPath as configured right now, trimmed of incidental whitespace/
    /// trailing separators. Matched case-insensitively (every sage50_path column
    /// is declared COLLATE NOCASE) since Windows paths are case-insensitive in
    /// practice, so "C:\simplyData\X.SAI" and "c:\simplydata\x.sai" are the same
    /// file, not two different ones. Never blank in real use (Sage50Client
    /// requires CompanyDataPath to connect at all) - "(not configured)" only
    /// shows up if something calls this before the Sage 50 tab has ever been
    /// filled in, which normal operation never does.</summary>
    private string CurrentSage50Path =>
        string.IsNullOrWhiteSpace(_sage50Settings.CompanyDataPath)
            ? "(not configured)"
            : _sage50Settings.CompanyDataPath.Trim().TrimEnd('\\', '/');

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS watermark (
                    key TEXT NOT NULL,
                    sage50_path TEXT NOT NULL COLLATE NOCASE,
                    value TEXT NOT NULL,
                    PRIMARY KEY (key, sage50_path)
                );

                CREATE TABLE IF NOT EXISTS imported_invoice (
                    portpro_invoice_id TEXT NOT NULL,
                    sage50_path TEXT NOT NULL COLLATE NOCASE,
                    reference_number TEXT NOT NULL,
                    sage50_invoice_number TEXT NOT NULL,
                    imported_at_utc TEXT NOT NULL,
                    PRIMARY KEY (portpro_invoice_id, sage50_path)
                );

                CREATE TABLE IF NOT EXISTS customer_sync_state (
                    portpro_customer_id TEXT NOT NULL,
                    sage50_path TEXT NOT NULL COLLATE NOCASE,
                    company_name TEXT NOT NULL,
                    portpro_updated_at TEXT NOT NULL,
                    synced_at_utc TEXT NOT NULL,
                    PRIMARY KEY (portpro_customer_id, sage50_path)
                );

                CREATE TABLE IF NOT EXISTS customer_refresh_status (
                    portpro_customer_id TEXT NOT NULL,
                    sage50_path TEXT NOT NULL COLLATE NOCASE,
                    company_name TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    success INTEGER NOT NULL,
                    message TEXT NOT NULL,
                    applied_at_utc TEXT NOT NULL,
                    PRIMARY KEY (portpro_customer_id, sage50_path)
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // Pre-existing installs (state.db created before 2026-08-24) have these
        // tables WITHOUT sage50_path - SQLite can't ALTER a primary key, so each
        // is rebuilt: renamed aside, recreated in the new shape, its rows copied
        // back in with sage50_path set to whatever's configured right now (the
        // only reasonable attribution - before this change there was only ever
        // one implicit "path" in use), then the old copy dropped. No data is
        // lost, only correctly attributed going forward.
        var currentPath = CurrentSage50Path;
        MigrateTableForSage50Path(conn, "watermark", "key, value");
        MigrateTableForSage50Path(conn, "imported_invoice", "portpro_invoice_id, reference_number, sage50_invoice_number, imported_at_utc");
        MigrateTableForSage50Path(conn, "customer_sync_state", "portpro_customer_id, company_name, portpro_updated_at, synced_at_utc");
        MigrateTableForSage50Path(conn, "customer_refresh_status", "portpro_customer_id, company_name, operation, success, message, applied_at_utc");

        void MigrateTableForSage50Path(SqliteConnection connection, string tableName, string oldColumns)
        {
            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = $"PRAGMA table_info({tableName});";
            var hasSage50Path = false;
            using (var reader = pragmaCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "sage50_path", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSage50Path = true;
                        break;
                    }
                }
            }
            // Freshly created above (new installs) already has the column, or
            // this table was already migrated in an earlier run - nothing to do.
            if (hasSage50Path) return;

            using var transaction = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = $"ALTER TABLE {tableName} RENAME TO {tableName}_pre_path_migration;";
                cmd.ExecuteNonQuery();

                // Recreate with the new schema - same DDL as the CREATE TABLE IF
                // NOT EXISTS above for this table, run again since the rename just
                // freed up the original name.
                cmd.CommandText = tableName switch
                {
                    "watermark" => """
                        CREATE TABLE watermark (
                            key TEXT NOT NULL, sage50_path TEXT NOT NULL COLLATE NOCASE, value TEXT NOT NULL,
                            PRIMARY KEY (key, sage50_path)
                        );
                        """,
                    "imported_invoice" => """
                        CREATE TABLE imported_invoice (
                            portpro_invoice_id TEXT NOT NULL, sage50_path TEXT NOT NULL COLLATE NOCASE,
                            reference_number TEXT NOT NULL, sage50_invoice_number TEXT NOT NULL, imported_at_utc TEXT NOT NULL,
                            PRIMARY KEY (portpro_invoice_id, sage50_path)
                        );
                        """,
                    "customer_sync_state" => """
                        CREATE TABLE customer_sync_state (
                            portpro_customer_id TEXT NOT NULL, sage50_path TEXT NOT NULL COLLATE NOCASE,
                            company_name TEXT NOT NULL, portpro_updated_at TEXT NOT NULL, synced_at_utc TEXT NOT NULL,
                            PRIMARY KEY (portpro_customer_id, sage50_path)
                        );
                        """,
                    "customer_refresh_status" => """
                        CREATE TABLE customer_refresh_status (
                            portpro_customer_id TEXT NOT NULL, sage50_path TEXT NOT NULL COLLATE NOCASE,
                            company_name TEXT NOT NULL, operation TEXT NOT NULL, success INTEGER NOT NULL,
                            message TEXT NOT NULL, applied_at_utc TEXT NOT NULL,
                            PRIMARY KEY (portpro_customer_id, sage50_path)
                        );
                        """,
                    _ => throw new InvalidOperationException($"Unknown table '{tableName}' in migration.")
                };
                cmd.ExecuteNonQuery();

                cmd.CommandText = $"INSERT INTO {tableName} ({oldColumns}, sage50_path) SELECT {oldColumns}, $path FROM {tableName}_pre_path_migration;";
                cmd.Parameters.AddWithValue("$path", currentPath);
                cmd.ExecuteNonQuery();

                cmd.CommandText = $"DROP TABLE {tableName}_pre_path_migration;";
                cmd.Parameters.Clear();
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();

            _logger.LogWarning(
                "Migrated {Table} to per-Sage50-path scoping - its existing rows were attributed to the currently " +
                "configured company file ('{Path}'), since that's the only file they could have come from before " +
                "this scoping existed.",
                tableName, currentPath);
        }
    }

    /// <summary>Every distinct Sage 50 company file path this database has ever
    /// recorded ANY data against, across all four tables - drives the Admin app's
    /// path picker on the Customer Refresh and History &amp; Logs tabs.</summary>
    public List<string> GetAllKnownSage50Paths()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT sage50_path FROM (
                SELECT sage50_path FROM watermark
                UNION SELECT sage50_path FROM imported_invoice
                UNION SELECT sage50_path FROM customer_sync_state
                UNION SELECT sage50_path FROM customer_refresh_status
            )
            WHERE sage50_path IS NOT NULL AND sage50_path <> ''
            ORDER BY sage50_path COLLATE NOCASE;
            """;

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    /// <summary>The PortPro updatedAt this customer's profile was last synced into
    /// Sage 50 as of - CustomerSyncService compares this against the customer's
    /// CURRENT updatedAt (from GetAllCustomersAsync) to detect a change since
    /// then. Null means never synced for the CURRENTLY configured Sage 50 path
    /// (either genuinely new, or only ever synced against a different company
    /// file).</summary>
    public DateTimeOffset? GetCustomerLastSyncedUpdatedAt(string portProCustomerId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT portpro_updated_at FROM customer_sync_state WHERE portpro_customer_id = $id AND sage50_path = $path;";
        cmd.Parameters.AddWithValue("$id", portProCustomerId);
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);
        var value = cmd.ExecuteScalar() as string;

        return value is null ? null : DateTimeOffset.Parse(value);
    }

    /// <summary>Records that this customer's profile (as of portProUpdatedAt) has
    /// been pushed into Sage 50 - called both right after an auto-create
    /// (InvoiceValidationService) and after CustomerSyncService pushes an update
    /// to an existing customer, so the same change is never re-applied twice.
    /// Scoped to the currently configured Sage 50 path.</summary>
    public void MarkCustomerSynced(string portProCustomerId, string companyName, DateTimeOffset portProUpdatedAt)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customer_sync_state (portpro_customer_id, sage50_path, company_name, portpro_updated_at, synced_at_utc)
            VALUES ($id, $path, $name, $updatedAt, $now)
            ON CONFLICT(portpro_customer_id, sage50_path) DO UPDATE SET
                company_name = excluded.company_name,
                portpro_updated_at = excluded.portpro_updated_at,
                synced_at_utc = excluded.synced_at_utc;
            """;
        cmd.Parameters.AddWithValue("$id", portProCustomerId);
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);
        cmd.Parameters.AddWithValue("$name", companyName);
        cmd.Parameters.AddWithValue("$updatedAt", portProUpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records the outcome of the Admin app's Customer Refresh "Run
    /// Selected" for one customer - one row per (PortPro customer id, Sage 50
    /// path), always overwritten by whatever happened most recently against THAT
    /// path (confirmed requirement 2026-08-24: "if it is selected then this date
    /// will be overwritten"), so this is deliberately a separate table from
    /// customer_sync_state rather than reusing its synced_at_utc - that one is
    /// shared with the automatic incidental sweep and would conflate "the
    /// background sweep touched this" with "the operator explicitly ran this from
    /// the Customer Refresh tab". Called for every REAL attempt, success or
    /// failure - see CustomerSyncService.ExecuteSelectedRefreshAsync, which
    /// deliberately does NOT call this for a Dry Run (confirmed 2026-08-24:
    /// nothing actually happened in Sage 50, so recording a status/date for it
    /// would misleadingly survive into a later session as if this customer had
    /// genuinely been created/updated). So "last operation" here is always the
    /// true most recent REAL attempt against the currently configured path, not
    /// just the most recent success, and never a simulation.</summary>
    public void RecordCustomerRefreshOutcome(string portProCustomerId, string companyName, string operation, bool success, string message, DateTimeOffset appliedAtUtc)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customer_refresh_status (portpro_customer_id, sage50_path, company_name, operation, success, message, applied_at_utc)
            VALUES ($id, $path, $name, $operation, $success, $message, $appliedAt)
            ON CONFLICT(portpro_customer_id, sage50_path) DO UPDATE SET
                company_name = excluded.company_name,
                operation = excluded.operation,
                success = excluded.success,
                message = excluded.message,
                applied_at_utc = excluded.applied_at_utc;
            """;
        cmd.Parameters.AddWithValue("$id", portProCustomerId);
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);
        cmd.Parameters.AddWithValue("$name", companyName);
        cmd.Parameters.AddWithValue("$operation", operation);
        cmd.Parameters.AddWithValue("$success", success ? 1 : 0);
        cmd.Parameters.AddWithValue("$message", message);
        cmd.Parameters.AddWithValue("$appliedAt", appliedAtUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every customer_refresh_status row for the CURRENTLY configured
    /// Sage 50 path, keyed by PortPro customer id - loaded once per
    /// ScanForRefreshAsync (not one query per customer, up to 223 of them) so the
    /// Admin app's Customer Refresh grid can show the last known operation/date
    /// for every candidate immediately after Extract, even before any Run
    /// Selected happens in the current session. Use GetCustomerRefreshOutcomesForPath
    /// instead to browse a DIFFERENT (non-current) path's history.</summary>
    public Dictionary<string, (string Operation, bool Success, string Message, DateTimeOffset AppliedAtUtc)> GetAllCustomerRefreshOutcomes()
        => GetCustomerRefreshOutcomesForPath(CurrentSage50Path);

    /// <summary>Same shape as GetAllCustomerRefreshOutcomes, but for an explicit
    /// path - lets the Admin app's Customer Refresh tab browse a PREVIOUSLY-used
    /// Sage 50 path's history (via its path picker) without that path having to
    /// be the one currently configured/connected.</summary>
    public Dictionary<string, (string Operation, bool Success, string Message, DateTimeOffset AppliedAtUtc)> GetCustomerRefreshOutcomesForPath(string sage50Path)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT portpro_customer_id, operation, success, message, applied_at_utc FROM customer_refresh_status WHERE sage50_path = $path;";
        cmd.Parameters.AddWithValue("$path", sage50Path);

        var results = new Dictionary<string, (string, bool, string, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2) != 0, reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)));
        }
        return results;
    }

    public DateTimeOffset? GetLastChangedWatermark()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM watermark WHERE key = 'last_changed_date' AND sage50_path = $path;";
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);
        var value = cmd.ExecuteScalar() as string;

        return value is null ? null : DateTimeOffset.Parse(value);
    }

    /// <summary>
    /// Advances the watermark - but only forward, and only for the currently
    /// configured Sage 50 path. Called immediately after each invoice is
    /// processed (not batched at the end of a run) so that if the process is
    /// killed mid-run (see Sage50Client.TerminateOnFatalWriteError), everything
    /// already processed before the failure is durably reflected here. The "only
    /// forward" guard exists because PortPro doesn't guarantee invoices come back
    /// ordered by updatedAt, so a later invoice in the same page can have an
    /// earlier updatedAt than one already recorded - never let that regress the
    /// anchor backward. Scoped per path so switching to a different (e.g. fresh
    /// PROD) company file genuinely starts Continue mode over, rather than
    /// resuming from wherever a different file's testing left off.
    /// </summary>
    public void SetLastChangedWatermark(DateTimeOffset value)
    {
        var current = GetLastChangedWatermark();
        if (current is not null && value <= current) return;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO watermark (key, sage50_path, value) VALUES ('last_changed_date', $path, $value)
            ON CONFLICT(key, sage50_path) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);
        cmd.Parameters.AddWithValue("$value", value.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Highest PortPro reference number seen across any watermark-driven run
    /// against the currently configured Sage 50 path, for display/audit purposes -
    /// see SyncRequest.UseWatermark's doc comment for why this doesn't actually
    /// drive the sync query (the date watermark does).
    /// </summary>
    public string? GetLastProcessedInvoiceNumber()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM watermark WHERE key = 'last_processed_invoice_number' AND sage50_path = $path;";
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Advances the last-processed-invoice-number for the currently configured
    /// Sage 50 path - but only forward (ordinal string comparison, matching how
    /// invoices are sorted before processing - see SyncOrchestrator.RunAsync).
    /// Same "only forward, update per-invoice not batched, scoped per path"
    /// reasoning as SetLastChangedWatermark above.
    /// </summary>
    public void SetLastProcessedInvoiceNumber(string value)
    {
        var current = GetLastProcessedInvoiceNumber();
        if (current is not null && string.CompareOrdinal(value, current) <= 0) return;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO watermark (key, sage50_path, value) VALUES ('last_processed_invoice_number', $path, $value)
            ON CONFLICT(key, sage50_path) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public bool IsAlreadyImported(string portProInvoiceId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM imported_invoice WHERE portpro_invoice_id = $id AND sage50_path = $path;";
        cmd.Parameters.AddWithValue("$id", portProInvoiceId);
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);

        var count = (long)cmd.ExecuteScalar()!;
        return count > 0;
    }

    public void MarkImported(string portProInvoiceId, string referenceNumber, string sage50InvoiceNumber)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO imported_invoice (portpro_invoice_id, sage50_path, reference_number, sage50_invoice_number, imported_at_utc)
            VALUES ($ppId, $path, $refNo, $sageNo, $now)
            ON CONFLICT(portpro_invoice_id, sage50_path) DO UPDATE SET
                sage50_invoice_number = excluded.sage50_invoice_number,
                imported_at_utc = excluded.imported_at_utc;
            """;
        cmd.Parameters.AddWithValue("$ppId", portProInvoiceId);
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);
        cmd.Parameters.AddWithValue("$refNo", referenceNumber);
        cmd.Parameters.AddWithValue("$sageNo", sage50InvoiceNumber);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();

        _logger.LogInformation(
            "Recorded import: PortPro invoice {PortProId} ({RefNo}) -> Sage 50 invoice {SageNo} (path: {Path})",
            portProInvoiceId, referenceNumber, sage50InvoiceNumber, CurrentSage50Path);
    }

    /// <summary>
    /// All imported_invoice reference numbers for the currently configured Sage 50
    /// path, ordinal-sorted ascending - for reporting the actual range/list of
    /// what's been transferred so far against THIS company file.
    /// </summary>
    public List<string> GetAllImportedReferenceNumbers()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT reference_number FROM imported_invoice WHERE sage50_path = $path ORDER BY reference_number;";
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    /// <summary>
    /// Removes imported_invoice records (for the currently configured Sage 50
    /// path only) whose reference_number falls in [start, end] (ordinal,
    /// inclusive) - for correcting false-positive MarkImported records, e.g.
    /// confirmed live 2026-08-04: a missing host disposal meant CloseDatabase()
    /// was never called after real-transfer/create-test-item commands, so writes
    /// that appeared to succeed (Post()/Save() returned true) were recorded as
    /// imported here without ever being durably committed to Sage 50. Returns the
    /// number of rows removed.
    /// </summary>
    public int RemoveImportedInReferenceRange(string startReferenceNumber, string endReferenceNumber)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM imported_invoice
            WHERE reference_number >= $start AND reference_number <= $end AND sage50_path = $path;
            """;
        cmd.Parameters.AddWithValue("$start", startReferenceNumber);
        cmd.Parameters.AddWithValue("$end", endReferenceNumber);
        cmd.Parameters.AddWithValue("$path", CurrentSage50Path);
        var removed = cmd.ExecuteNonQuery();

        _logger.LogWarning(
            "Removed {Count} imported_invoice record(s) with reference_number in [{Start}, {End}] for path '{Path}'.",
            removed, startReferenceNumber, endReferenceNumber, CurrentSage50Path);

        return removed;
    }
}
