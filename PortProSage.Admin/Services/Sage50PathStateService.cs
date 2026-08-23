using Microsoft.Data.Sqlite;

namespace PortProSage.Admin.Services;

/// <summary>Direct read access to the sage50_path column PortProSage.Core's
/// SyncStateRepository now stamps on every state.db table (watermark,
/// imported_invoice, customer_sync_state, customer_refresh_status) - Admin can't
/// reference Core (different target frameworks: net10.0-windows here vs net48
/// there), same reasoning as ImportedInvoiceStateService. Drives the path picker
/// on the Customer Refresh and History &amp; Logs tabs, so an operator who's
/// worked against more than one Sage 50 company file (e.g. a DEV file during
/// testing, then a PROD file for real use) can browse either one's history
/// without reconfiguring the Sage 50 tab.</summary>
public static class Sage50PathStateService
{
    private static readonly string[] TablesWithSage50Path =
        { "watermark", "imported_invoice", "customer_sync_state", "customer_refresh_status" };

    /// <summary>Every distinct Sage 50 company file path state.db has ever
    /// recorded any data against. Empty if the database doesn't exist yet, or if
    /// it still has the pre-2026-08-24 schema (no sage50_path column at all) -
    /// that only happens if the Service hasn't been run even once since this
    /// feature shipped, since SyncStateRepository migrates the schema on its own
    /// first use; nothing to show here until it has.</summary>
    public static List<string> GetAllKnownPaths(string stateDatabasePath)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(stateDatabasePath) || !File.Exists(stateDatabasePath))
        {
            return results;
        }

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        var unionParts = new List<string>();
        foreach (var table in TablesWithSage50Path)
        {
            if (TableHasSage50PathColumn(conn, table))
            {
                unionParts.Add($"SELECT sage50_path FROM {table}");
            }
        }
        if (unionParts.Count == 0) return results; // no migrated table yet

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT DISTINCT sage50_path FROM ({string.Join(" UNION ", unionParts)}) " +
            "WHERE sage50_path IS NOT NULL AND sage50_path <> '' ORDER BY sage50_path COLLATE NOCASE;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    /// <summary>One row per customer_refresh_status entry recorded against the
    /// given (typically NON-current, historical) Sage 50 path - used by the
    /// Customer Refresh tab's path picker to show what was actually done the
    /// last time this app targeted that path, since a live Extract/comparison is
    /// only possible against whichever path is currently configured/connected.</summary>
    public record CustomerRefreshStatusRow(string PortProCustomerId, string CompanyName, string Operation, bool Success, string Message, DateTimeOffset AppliedAtUtc);

    public static List<CustomerRefreshStatusRow> GetCustomerRefreshStatusForPath(string stateDatabasePath, string sage50Path)
    {
        var results = new List<CustomerRefreshStatusRow>();
        if (string.IsNullOrWhiteSpace(stateDatabasePath) || !File.Exists(stateDatabasePath))
        {
            return results;
        }

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        if (!TableHasSage50PathColumn(conn, "customer_refresh_status")) return results;

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT portpro_customer_id, company_name, operation, success, message, applied_at_utc " +
            "FROM customer_refresh_status WHERE sage50_path = $path ORDER BY company_name COLLATE NOCASE;";
        cmd.Parameters.AddWithValue("$path", sage50Path);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CustomerRefreshStatusRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3) != 0, reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5))));
        }
        return results;
    }

    private static bool TableHasSage50PathColumn(SqliteConnection conn, string tableName)
    {
        using var existsCmd = conn.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=$name;";
        existsCmd.Parameters.AddWithValue("$name", tableName);
        if (Convert.ToInt64(existsCmd.ExecuteScalar()) == 0) return false;

        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = pragmaCmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "sage50_path", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
