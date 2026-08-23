using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;

namespace PortProSage.Core.Data;

/// <summary>
/// Tracks two things locally so the service can run unattended and idempotently:
///   1. The "last changed date" watermark used for the automatic polling sync.
///   2. Which PortPro invoice ids have already been imported into Sage 50, so a
///      re-fetched or re-triggered invoice is never double-booked.
/// </summary>
public class SyncStateRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SyncStateRepository> _logger;

    public SyncStateRepository(SyncSettings settings, ILogger<SyncStateRepository> logger)
    {
        var dir = Path.GetDirectoryName(settings.StateDatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={settings.StateDatabasePath}";
        _logger = logger;
        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS watermark (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS imported_invoice (
                portpro_invoice_id TEXT PRIMARY KEY,
                reference_number TEXT NOT NULL,
                sage50_invoice_number TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS customer_sync_state (
                portpro_customer_id TEXT PRIMARY KEY,
                company_name TEXT NOT NULL,
                portpro_updated_at TEXT NOT NULL,
                synced_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS customer_refresh_status (
                portpro_customer_id TEXT PRIMARY KEY,
                company_name TEXT NOT NULL,
                operation TEXT NOT NULL,
                success INTEGER NOT NULL,
                message TEXT NOT NULL,
                applied_at_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The PortPro updatedAt this customer's profile was last synced into
    /// Sage 50 as of - CustomerSyncService compares this against the customer's
    /// CURRENT updatedAt (from GetAllCustomersAsync) to detect a change since
    /// then. Null means never synced (either genuinely new, or synced before this
    /// table existed).</summary>
    public DateTimeOffset? GetCustomerLastSyncedUpdatedAt(string portProCustomerId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT portpro_updated_at FROM customer_sync_state WHERE portpro_customer_id = $id;";
        cmd.Parameters.AddWithValue("$id", portProCustomerId);
        var value = cmd.ExecuteScalar() as string;

        return value is null ? null : DateTimeOffset.Parse(value);
    }

    /// <summary>Records that this customer's profile (as of portProUpdatedAt) has
    /// been pushed into Sage 50 - called both right after an auto-create
    /// (InvoiceValidationService) and after CustomerSyncService pushes an update
    /// to an existing customer, so the same change is never re-applied twice.</summary>
    public void MarkCustomerSynced(string portProCustomerId, string companyName, DateTimeOffset portProUpdatedAt)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customer_sync_state (portpro_customer_id, company_name, portpro_updated_at, synced_at_utc)
            VALUES ($id, $name, $updatedAt, $now)
            ON CONFLICT(portpro_customer_id) DO UPDATE SET
                company_name = excluded.company_name,
                portpro_updated_at = excluded.portpro_updated_at,
                synced_at_utc = excluded.synced_at_utc;
            """;
        cmd.Parameters.AddWithValue("$id", portProCustomerId);
        cmd.Parameters.AddWithValue("$name", companyName);
        cmd.Parameters.AddWithValue("$updatedAt", portProUpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Records the outcome of the Admin app's Customer Refresh "Run
    /// Selected" for one customer - one row per PortPro customer id, always
    /// overwritten by whatever happened most recently (confirmed requirement
    /// 2026-08-24: "if it is selected then this date will be overwritten"), so
    /// this is deliberately a separate table from customer_sync_state rather than
    /// reusing its synced_at_utc - that one is shared with the automatic
    /// incidental sweep and would conflate "the background sweep touched this"
    /// with "the operator explicitly ran this from the Customer Refresh tab".
    /// Called for every REAL attempt, success or failure - see
    /// CustomerSyncService.ExecuteSelectedRefreshAsync, which deliberately does
    /// NOT call this for a Dry Run (confirmed 2026-08-24: nothing actually
    /// happened in Sage 50, so recording a status/date for it would misleadingly
    /// survive into a later session as if this customer had genuinely been
    /// created/updated). So "last operation" here is always the true most recent
    /// REAL attempt, not just the most recent success, and never a simulation.</summary>
    public void RecordCustomerRefreshOutcome(string portProCustomerId, string companyName, string operation, bool success, string message, DateTimeOffset appliedAtUtc)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO customer_refresh_status (portpro_customer_id, company_name, operation, success, message, applied_at_utc)
            VALUES ($id, $name, $operation, $success, $message, $appliedAt)
            ON CONFLICT(portpro_customer_id) DO UPDATE SET
                company_name = excluded.company_name,
                operation = excluded.operation,
                success = excluded.success,
                message = excluded.message,
                applied_at_utc = excluded.applied_at_utc;
            """;
        cmd.Parameters.AddWithValue("$id", portProCustomerId);
        cmd.Parameters.AddWithValue("$name", companyName);
        cmd.Parameters.AddWithValue("$operation", operation);
        cmd.Parameters.AddWithValue("$success", success ? 1 : 0);
        cmd.Parameters.AddWithValue("$message", message);
        cmd.Parameters.AddWithValue("$appliedAt", appliedAtUtc.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every customer_refresh_status row, keyed by PortPro customer id -
    /// loaded once per ScanForRefreshAsync (not one query per customer, up to 223
    /// of them) so the Admin app's Customer Refresh grid can show the last known
    /// operation/date for every candidate immediately after Extract, even before
    /// any Run Selected happens in the current session.</summary>
    public Dictionary<string, (string Operation, bool Success, string Message, DateTimeOffset AppliedAtUtc)> GetAllCustomerRefreshOutcomes()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT portpro_customer_id, operation, success, message, applied_at_utc FROM customer_refresh_status;";

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
        cmd.CommandText = "SELECT value FROM watermark WHERE key = 'last_changed_date';";
        var value = cmd.ExecuteScalar() as string;

        return value is null ? null : DateTimeOffset.Parse(value);
    }

    /// <summary>
    /// Advances the watermark - but only forward. Called immediately after each
    /// invoice is processed (not batched at the end of a run) so that if the
    /// process is killed mid-run (see Sage50Client.TerminateOnFatalWriteError),
    /// everything already processed before the failure is durably reflected here.
    /// The "only forward" guard exists because PortPro doesn't guarantee invoices
    /// come back ordered by updatedAt, so a later invoice in the same page can have
    /// an earlier updatedAt than one already recorded - never let that regress the
    /// anchor backward.
    /// </summary>
    public void SetLastChangedWatermark(DateTimeOffset value)
    {
        var current = GetLastChangedWatermark();
        if (current is not null && value <= current) return;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO watermark (key, value) VALUES ('last_changed_date', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$value", value.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Highest PortPro reference number seen across any watermark-driven run, for
    /// display/audit purposes - see SyncRequest.UseWatermark's doc comment for why
    /// this doesn't actually drive the sync query (the date watermark does).
    /// </summary>
    public string? GetLastProcessedInvoiceNumber()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM watermark WHERE key = 'last_processed_invoice_number';";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Advances the last-processed-invoice-number - but only forward (ordinal string
    /// comparison, matching how invoices are sorted before processing - see
    /// SyncOrchestrator.RunAsync). Same "only forward, update per-invoice not batched"
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
            INSERT INTO watermark (key, value) VALUES ('last_processed_invoice_number', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    public bool IsAlreadyImported(string portProInvoiceId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM imported_invoice WHERE portpro_invoice_id = $id;";
        cmd.Parameters.AddWithValue("$id", portProInvoiceId);

        var count = (long)cmd.ExecuteScalar()!;
        return count > 0;
    }

    public void MarkImported(string portProInvoiceId, string referenceNumber, string sage50InvoiceNumber)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO imported_invoice (portpro_invoice_id, reference_number, sage50_invoice_number, imported_at_utc)
            VALUES ($ppId, $refNo, $sageNo, $now)
            ON CONFLICT(portpro_invoice_id) DO UPDATE SET
                sage50_invoice_number = excluded.sage50_invoice_number,
                imported_at_utc = excluded.imported_at_utc;
            """;
        cmd.Parameters.AddWithValue("$ppId", portProInvoiceId);
        cmd.Parameters.AddWithValue("$refNo", referenceNumber);
        cmd.Parameters.AddWithValue("$sageNo", sage50InvoiceNumber);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();

        _logger.LogInformation(
            "Recorded import: PortPro invoice {PortProId} ({RefNo}) -> Sage 50 invoice {SageNo}",
            portProInvoiceId, referenceNumber, sage50InvoiceNumber);
    }

    /// <summary>
    /// All imported_invoice reference numbers, ordinal-sorted ascending - for
    /// reporting the actual range/list of what's been transferred so far.
    /// </summary>
    public List<string> GetAllImportedReferenceNumbers()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT reference_number FROM imported_invoice ORDER BY reference_number;";

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    /// <summary>
    /// Removes imported_invoice records whose reference_number falls in [start, end]
    /// (ordinal, inclusive) - for correcting false-positive MarkImported records,
    /// e.g. confirmed live 2026-08-04: a missing host disposal meant CloseDatabase()
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
            WHERE reference_number >= $start AND reference_number <= $end;
            """;
        cmd.Parameters.AddWithValue("$start", startReferenceNumber);
        cmd.Parameters.AddWithValue("$end", endReferenceNumber);
        var removed = cmd.ExecuteNonQuery();

        _logger.LogWarning(
            "Removed {Count} imported_invoice record(s) with reference_number in [{Start}, {End}].",
            removed, startReferenceNumber, endReferenceNumber);

        return removed;
    }
}
