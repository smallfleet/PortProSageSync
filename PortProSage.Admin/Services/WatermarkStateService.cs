using Microsoft.Data.Sqlite;

namespace PortProSage.Admin.Services;

/// <summary>Direct read/write access to the same `watermark` table
/// PortProSage.Core's SyncStateRepository owns - Admin can't reference Core
/// (different target frameworks: net10.0-windows here vs net48 there), so this
/// is a minimal, independent read/write of just the two rows the watermark
/// field needs. Unlike SyncStateRepository.SetLastChangedWatermark/
/// SetLastProcessedInvoiceNumber, WriteNew below is NOT guarded to only move
/// forward - this exists specifically to let an operator deliberately rewind
/// or clear the watermark, which the normal sync path can never do.
///
/// Scoped by sage50_path, matching Core's per-Sage50-path scoping (see
/// TECHNICAL_DESIGN.md §6.12) - the table's real primary key is (key,
/// sage50_path), not key alone. Confirmed live 2026-08-25: this file predated
/// that migration and still assumed the old single-column-key schema, which
/// both silently read the WRONG path's value (ReadCurrent's query had no path
/// filter at all, so with more than one path's rows present it returned
/// whichever SQLite happened to return first) and crashed outright on write
/// (SQLite Error 1: ON CONFLICT clause does not match any PRIMARY KEY or
/// UNIQUE constraint) once "Save Automatic Sync settings" started writing the
/// watermark on every save instead of only on a rare, dedicated action.</summary>
public static class WatermarkStateService
{
    public static (DateTimeOffset? Date, string? InvoiceNumber) ReadCurrent(string stateDatabasePath, string sage50Path)
    {
        if (string.IsNullOrWhiteSpace(stateDatabasePath) || !File.Exists(stateDatabasePath) || string.IsNullOrWhiteSpace(sage50Path))
        {
            return (null, null);
        }

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        DateTimeOffset? date = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM watermark WHERE key = 'last_changed_date' AND sage50_path = $path;";
            cmd.Parameters.AddWithValue("$path", sage50Path);
            if (cmd.ExecuteScalar() is string raw && DateTimeOffset.TryParse(raw, out var parsed))
            {
                date = parsed;
            }
        }

        string? invoiceNumber;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM watermark WHERE key = 'last_processed_invoice_number' AND sage50_path = $path;";
            cmd.Parameters.AddWithValue("$path", sage50Path);
            invoiceNumber = cmd.ExecuteScalar() as string;
        }

        return (date, invoiceNumber);
    }

    /// <summary>Overwrites both watermark rows (for this sage50Path only)
    /// unconditionally - deletes a row entirely when the corresponding value is
    /// null/blank (so the next run treats it exactly like "no run has ever
    /// happened", the same state a brand new state.db would be in), otherwise
    /// inserts/replaces it. No forward-only guard: the whole point is a
    /// deliberate operator override, including moving the watermark backward.</summary>
    public static void WriteNew(string stateDatabasePath, string sage50Path, DateTimeOffset? date, string? invoiceNumber)
    {
        if (string.IsNullOrWhiteSpace(sage50Path)) return;

        var dir = Path.GetDirectoryName(stateDatabasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        using (var createCmd = conn.CreateCommand())
        {
            // Matches Core's SyncStateRepository schema exactly (key, sage50_path,
            // value) with PRIMARY KEY (key, sage50_path) - only ever actually
            // creates the table here if Admin writes a watermark before the
            // Service has ever run against this state.db at all; otherwise it's a
            // no-op and the real table (already migrated by Core) is used as-is.
            createCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS watermark (
                    key TEXT NOT NULL, sage50_path TEXT NOT NULL COLLATE NOCASE, value TEXT NOT NULL,
                    PRIMARY KEY (key, sage50_path)
                );
                """;
            createCmd.ExecuteNonQuery();
        }

        SetOrClear(conn, "last_changed_date", sage50Path, date?.ToString("O"));
        SetOrClear(conn, "last_processed_invoice_number", sage50Path, string.IsNullOrWhiteSpace(invoiceNumber) ? null : invoiceNumber);
    }

    private static void SetOrClear(SqliteConnection conn, string key, string sage50Path, string? value)
    {
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrEmpty(value))
        {
            cmd.CommandText = "DELETE FROM watermark WHERE key = $key AND sage50_path = $path;";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$path", sage50Path);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO watermark (key, sage50_path, value) VALUES ($key, $path, $value)
                ON CONFLICT(key, sage50_path) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$path", sage50Path);
            cmd.Parameters.AddWithValue("$value", value);
        }
        cmd.ExecuteNonQuery();
    }
}
