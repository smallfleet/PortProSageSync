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
            """;
        cmd.ExecuteNonQuery();
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
}
