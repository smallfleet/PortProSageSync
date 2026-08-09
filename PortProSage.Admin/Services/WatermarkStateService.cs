using Microsoft.Data.Sqlite;

namespace PortProSage.Admin.Services;

/// <summary>Direct read/write access to the same `watermark` table
/// PortProSage.Core's SyncStateRepository owns - Admin can't reference Core
/// (different target frameworks: net10.0-windows here vs net48 there), so this
/// is a minimal, independent read/write of just the two rows the Watermark tab
/// needs. Unlike SyncStateRepository.SetLastChangedWatermark/
/// SetLastProcessedInvoiceNumber, WriteNew below is NOT guarded to only move
/// forward - this exists specifically to let an operator deliberately rewind
/// or clear the watermark, which the normal sync path can never do.</summary>
public static class WatermarkStateService
{
    public static (DateTimeOffset? Date, string? InvoiceNumber) ReadCurrent(string stateDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(stateDatabasePath) || !File.Exists(stateDatabasePath))
        {
            return (null, null);
        }

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        DateTimeOffset? date = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM watermark WHERE key = 'last_changed_date';";
            if (cmd.ExecuteScalar() is string raw && DateTimeOffset.TryParse(raw, out var parsed))
            {
                date = parsed;
            }
        }

        string? invoiceNumber;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM watermark WHERE key = 'last_processed_invoice_number';";
            invoiceNumber = cmd.ExecuteScalar() as string;
        }

        return (date, invoiceNumber);
    }

    /// <summary>Overwrites both watermark rows unconditionally - deletes a row
    /// entirely when the corresponding value is null/blank (so the next run
    /// treats it exactly like "no run has ever happened", the same state a
    /// brand new state.db would be in), otherwise inserts/replaces it. No
    /// forward-only guard: the whole point is a deliberate operator override,
    /// including moving the watermark backward.</summary>
    public static void WriteNew(string stateDatabasePath, DateTimeOffset? date, string? invoiceNumber)
    {
        var dir = Path.GetDirectoryName(stateDatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        using (var createCmd = conn.CreateCommand())
        {
            createCmd.CommandText = "CREATE TABLE IF NOT EXISTS watermark (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            createCmd.ExecuteNonQuery();
        }

        SetOrClear(conn, "last_changed_date", date?.ToString("O"));
        SetOrClear(conn, "last_processed_invoice_number", string.IsNullOrWhiteSpace(invoiceNumber) ? null : invoiceNumber);
    }

    private static void SetOrClear(SqliteConnection conn, string key, string? value)
    {
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrEmpty(value))
        {
            cmd.CommandText = "DELETE FROM watermark WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO watermark (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
        }
        cmd.ExecuteNonQuery();
    }
}
