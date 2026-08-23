using Microsoft.Data.Sqlite;

namespace PortProSage.Admin.Services;

/// <summary>Direct read/write access to the same `imported_invoice` table
/// PortProSage.Core's SyncStateRepository owns - Admin can't reference Core
/// (different target frameworks: net10.0-windows here vs net48 there). This is
/// what SyncOrchestrator.IsAlreadyImported checks before posting anything - it's
/// purely local bookkeeping ("did WE already create this in Sage 50"), never
/// re-verified against what's actually in Sage 50 itself. Confirmed live
/// 2026-08-10: switching to a fresh/new Sage 50 company file while keeping the
/// same state.db left every invoice from before permanently marked
/// ALREADY_IMPORTED and skipped, even though the new file had never seen any
/// of them - this exists to let an operator deliberately clear that tracking
/// when it's known to be stale relative to the actual Sage 50 file in use.</summary>
public static class ImportedInvoiceStateService
{
    public static int CountImported(string stateDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(stateDatabasePath) || !File.Exists(stateDatabasePath))
        {
            return 0;
        }

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        // sqlite_master check first - the table may not exist yet on a brand new
        // state.db (SyncStateRepository creates it lazily on first real use).
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='imported_invoice';";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return 0;

        cmd.CommandText = "SELECT COUNT(1) FROM imported_invoice;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Deletes every row - the next run treats every invoice as brand
    /// new, exactly the state a fresh state.db would be in. Returns the number
    /// of rows actually removed, for confirmation.</summary>
    public static int ClearAll(string stateDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(stateDatabasePath) || !File.Exists(stateDatabasePath))
        {
            return 0;
        }

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='imported_invoice';";
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return 0;

        cmd.CommandText = "DELETE FROM imported_invoice;";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes only the rows matching the given reference numbers - used by
    /// History &amp; Logs' Delete feature (MainForm.HistoryTab.cs / RunDeletionService)
    /// to remove a specific run's own tracking rows, as opposed to ClearAll's wipe-
    /// everything. Matched by reference_number (not portpro_invoice_id) since that's
    /// the only identifier available from both a real result.json's Outcomes AND a
    /// log-reconstructed run's parsed TRANSFER lines - see RunDeletionService.
    ///
    /// sage50Path scopes the delete to just that Sage 50 company file, when
    /// provided AND the table has been migrated to carry sage50_path at all
    /// (confirmed 2026-08-24 this matters: since a reference number is only
    /// unique WITHIN one Sage 50 path now, not globally, an unscoped delete could
    /// remove a different path's genuinely-still-valid tracking row for the same
    /// reference number - e.g. the same invoice number legitimately imported to
    /// both a DEV and a PROD company file at different times). Null means
    /// "delete regardless of path" - used for a pre-migration database (no
    /// sage50_path column exists yet to scope by) or a log-reconstructed entry
    /// with no recorded Sage50Path of its own.
    ///
    /// Returns the number of rows actually removed.</summary>
    public static int DeleteByReferenceNumbers(string stateDatabasePath, IEnumerable<string> referenceNumbers, string? sage50Path = null)
    {
        var refs = referenceNumbers.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (refs.Count == 0 || string.IsNullOrWhiteSpace(stateDatabasePath) || !File.Exists(stateDatabasePath))
        {
            return 0;
        }

        using var conn = new SqliteConnection($"Data Source={stateDatabasePath}");
        conn.Open();

        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='imported_invoice';";
        if (Convert.ToInt64(checkCmd.ExecuteScalar()) == 0) return 0;

        var scopeByPath = !string.IsNullOrWhiteSpace(sage50Path) && TableHasSage50PathColumn(conn, "imported_invoice");

        var removed = 0;
        using var transaction = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = scopeByPath
                ? "DELETE FROM imported_invoice WHERE reference_number = $ref COLLATE NOCASE AND sage50_path = $path;"
                : "DELETE FROM imported_invoice WHERE reference_number = $ref COLLATE NOCASE;";
            var refParam = cmd.CreateParameter();
            refParam.ParameterName = "$ref";
            cmd.Parameters.Add(refParam);
            if (scopeByPath)
            {
                var pathParam = cmd.CreateParameter();
                pathParam.ParameterName = "$path";
                pathParam.Value = sage50Path;
                cmd.Parameters.Add(pathParam);
            }

            foreach (var reference in refs)
            {
                refParam.Value = reference;
                removed += cmd.ExecuteNonQuery();
            }
        }
        transaction.Commit();

        return removed;
    }

    private static bool TableHasSage50PathColumn(SqliteConnection conn, string tableName)
    {
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
