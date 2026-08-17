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
    /// Returns the number of rows actually removed.</summary>
    public static int DeleteByReferenceNumbers(string stateDatabasePath, IEnumerable<string> referenceNumbers)
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

        var removed = 0;
        using var transaction = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM imported_invoice WHERE reference_number = $ref COLLATE NOCASE;";
            var param = cmd.CreateParameter();
            param.ParameterName = "$ref";
            cmd.Parameters.Add(param);

            foreach (var reference in refs)
            {
                param.Value = reference;
                removed += cmd.ExecuteNonQuery();
            }
        }
        transaction.Commit();

        return removed;
    }
}
