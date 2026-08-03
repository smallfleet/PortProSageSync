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

    public void SetLastChangedWatermark(DateTimeOffset value)
    {
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
