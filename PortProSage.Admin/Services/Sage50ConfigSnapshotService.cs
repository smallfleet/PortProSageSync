using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PortProSage.Admin.Services;

/// <summary>Direct read/write access to a small Admin-only table
/// (admin_sage50_config_snapshot) in state.db - not part of PortProSage.Core's
/// own schema/migration (SyncStateRepository), and never read by the Service;
/// this exists purely so the Sage 50 tab's path dropdown can restore that path's
/// own full configuration when picked, instead of the operator having to
/// re-enter App name, App ID, User Name, Password, account defaults, tax codes,
/// and the charge account map by hand every time they switch between two Sage 50
/// company files (e.g. DEV vs PROD) - which used to genuinely need re-entering,
/// since appsettings.json/appsettings.Local.json only ever hold ONE current
/// configuration, overwritten in place on every Save regardless of which path it
/// was actually for. Keyed by sage50_path, one row per distinct path, always
/// overwritten (not appended) on save - only the single most recent snapshot for
/// that path is kept, mirroring customer_refresh_status's own "last outcome"
/// semantics (see Core's SyncStateRepository).</summary>
public static class Sage50ConfigSnapshotService
{
    public class TaxCodeRow
    {
        public string Abbreviation { get; set; } = "";
        public string Sage50Code { get; set; } = "";
    }

    public class ChargeMapRow
    {
        public string PortProChargeName { get; set; } = "";
        public string PortProChargeNumber { get; set; } = "";
        public string Sage50AccountName { get; set; } = "";
        public string Sage50AccountNumber { get; set; } = "";
    }

    public class Snapshot
    {
        public string AppName { get; set; } = "";
        public string AppId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public string ExpectedSdkVersion { get; set; } = "";
        public string DefaultRevenueAccount { get; set; } = "";
        public string DefaultReceivableAccount { get; set; } = "";
        public int DefaultNetTermDays { get; set; }
        public bool AutoCreateCustomers { get; set; }
        public bool SyncCustomerUpdatesFromPortPro { get; set; }
        public bool AutoCreateItems { get; set; }
        public bool DryRun { get; set; }
        public bool IgnoreAccountMismatchUseDefault { get; set; }
        public string AccountsUnverifiableBySdk { get; set; } = "";
        public List<TaxCodeRow> TaxCodes { get; set; } = new();
        public List<ChargeMapRow> ChargeAccountMap { get; set; } = new();
    }

    public static void SaveSnapshot(string stateDatabasePath, string sage50Path, Snapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(stateDatabasePath) || string.IsNullOrWhiteSpace(sage50Path)) return;

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();
        EnsureTable(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO admin_sage50_config_snapshot (sage50_path, config_json, saved_at_utc) " +
            "VALUES ($path, $json, $savedAt) " +
            "ON CONFLICT(sage50_path) DO UPDATE SET config_json = excluded.config_json, saved_at_utc = excluded.saved_at_utc;";
        cmd.Parameters.AddWithValue("$path", sage50Path);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot));
        cmd.Parameters.AddWithValue("$savedAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Null if this path has never had a snapshot saved (e.g. the very
    /// first time it's configured), or if state.db/the table doesn't exist yet -
    /// the caller leaves the form's other fields untouched in that case, rather
    /// than clearing them to blank.</summary>
    public static Snapshot? TryLoadSnapshot(string stateDatabasePath, string sage50Path)
    {
        if (string.IsNullOrWhiteSpace(stateDatabasePath) || !File.Exists(stateDatabasePath) || string.IsNullOrWhiteSpace(sage50Path))
        {
            return null;
        }

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='admin_sage50_config_snapshot';";
        if (Convert.ToInt64(checkCmd.ExecuteScalar()) == 0) return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT config_json FROM admin_sage50_config_snapshot WHERE sage50_path = $path;";
        cmd.Parameters.AddWithValue("$path", sage50Path);

        if (cmd.ExecuteScalar() is not string json || string.IsNullOrEmpty(json)) return null;

        try { return JsonSerializer.Deserialize<Snapshot>(json); }
        catch { return null; } // corrupt row - treat as "no snapshot" rather than crash
    }

    private static void EnsureTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS admin_sage50_config_snapshot (" +
            "sage50_path TEXT PRIMARY KEY COLLATE NOCASE, " +
            "config_json TEXT NOT NULL, " +
            "saved_at_utc TEXT NOT NULL" +
            ");";
        cmd.ExecuteNonQuery();
    }
}
