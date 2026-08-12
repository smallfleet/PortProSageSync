using PortProSage.Admin.Services;

namespace PortProSage.Admin;

public partial class MainForm
{
    private CheckBox _emailEnabled = new() { Text = "Enabled (send failed-transaction reports by email)" };
    private TextBox _emailSmtpHost = new() { Width = FieldHalfWidth };
    private NumericUpDown _emailSmtpPort = new() { Minimum = 1, Maximum = 65535, Value = 587 };
    private CheckBox _emailUseSsl = new() { Text = "Use SSL" };
    private TextBox _emailFromAddress = new() { Width = FieldHalfWidth };
    private TextBox _emailUsername = new() { Width = FieldHalfWidth };
    private TextBox _emailPassword = new() { UseSystemPasswordChar = true, Width = FieldHalfWidth };
    private TextBox _emailRecipients = new() { Width = FieldHalfWidth };

    // Read-only - reflects whatever is actually in state.db's imported_invoice
    // table right now, re-read every time RefreshImportedInvoiceCount() runs
    // (tab load and the Refresh button here), not a cached/stale value.
    private TextBox _importedInvoiceCount = new() { ReadOnly = true, Enabled = false, Width = 320 };

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("Settings");
        var grid = NewFieldGrid();
        const string f = AppSettingsFileName;

        _syncMinimumLogLevel.Items.AddRange(new object[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" });

        AddSectionHeading(grid, "Email");

        AddCheckRow(grid, _emailEnabled, f, "PortProSage:Email:Enabled",
            "The master switch for this whole feature. When unchecked, a failed-transaction report is still saved as " +
            "a CSV file on disk (see Folder Locations below -> Failed transactions folder), but no email is ever " +
            "sent - every other Email field on this tab is ignored. Check this only once SMTP host/credentials " +
            "below are filled in with real values.");
        AddRow(grid, "SMTP host", _emailSmtpHost, f, "PortProSage:Email:SmtpHost",
            "The address of the mail server used to send failed-transaction reports.\n\n" +
            "Example: smtp.office365.com  (Microsoft 365) or  smtp.gmail.com  (Gmail).",
            stretchInput: false);
        AddRow(grid, "SMTP port", _emailSmtpPort, f, "PortProSage:Email:SmtpPort",
            "The network port the SMTP host listens on.\n\nExample: 587 (the standard port for SSL/TLS-secured SMTP submission).");
        AddCheckRow(grid, _emailUseSsl, f, "PortProSage:Email:UseSsl",
            "Whether the connection to the SMTP host is encrypted. Almost every modern mail provider requires this " +
            "checked - only uncheck it if your mail server specifically documents that it needs a plain, unencrypted connection.");
        AddRow(grid, "From address", _emailFromAddress, f, "PortProSage:Email:FromAddress",
            "The email address that failed-transaction reports appear to be sent from.\n\n" +
            "Example: portprosync@rushtransfer.com",
            stretchInput: false);
        AddRow(grid, "Username", _emailUsername, f, "PortProSage:Email:Username",
            "The login username for the SMTP host above - for most providers this is the same as the From address, " +
            "but not always (e.g. some providers use a separate app-specific username).",
            stretchInput: false);
        AddRow(grid, "Password (secret)", _emailPassword, LocalSettingsFileName, "PortProSage:Email:Password",
            "The password (or app-specific password, for providers like Gmail/Microsoft 365 that require one instead " +
            "of your real account password) for the SMTP username above.",
            stretchInput: false);
        AddRow(grid, "Recipients\n(comma-separated)", _emailRecipients, f, "PortProSage:Email:RecipientAddressesCsv",
            "Every email address that should receive a failed-transaction report, separated by commas. Sent to ALL " +
            "of these addresses every time a sync run has at least one failure.\n\n" +
            "Example: ashwani@smallarc.com, accounting@rushtransfer.com",
            stretchInput: false);

        AddSectionHeading(grid, "Folder Locations");

        AddFolderRow(grid, "Trigger folder", _syncTriggerFolder, f, "PortProSage:Sync:TriggerFolder",
            "The folder the running Service watches for new manual sync requests - both the command-line Trigger " +
            "tool and this Admin app's Manual Run tab drop a request file here.\n\n" +
            "Example: C:\\PortProSageSync\\requests");
        AddFolderRow(grid, "Processed trigger folder", _syncProcessedTriggerFolder, f, "PortProSage:Sync:ProcessedTriggerFolder",
            "Where a request file (and its result) get moved once the Service has finished processing it. The " +
            "History & Logs tab reads completed runs from here.\n\n" +
            "Example: C:\\PortProSageSync\\requests\\processed");
        AddFolderRow(grid, "State database path", _syncStateDatabasePath, f, "PortProSage:Sync:StateDatabasePath",
            "The SQLite database file that remembers which PortPro invoices have already been imported, and the " +
            "watermark used by \"continue from where we left off\". Do not point two different client deployments " +
            "at the same file.\n\nExample: C:\\PortProSageSync\\state.db",
            isFile: true);
        AddFolderRow(grid, "Log folder", _syncLogFolder, f, "PortProSage:Sync:LogFolder",
            "Where the Service writes its daily rolling log files (one file per day, kept for 30 days). The " +
            "History & Logs tab reads these to show each run's full log.\n\nExample: C:\\PortProSageSync\\logs");
        AddFolderRow(grid, "Failed transactions folder", _syncFailedTransactionsFolder, f, "PortProSage:Sync:FailedTransactionsFolder",
            "Where a CSV report of any failed invoices from a run gets saved, with a microsecond-precision timestamp " +
            "in the filename. Written every time a run has at least one failure, regardless of whether email is enabled.\n\n" +
            "Example: C:\\PortProSageSync\\failed-transactions");
        AddRow(grid, "Minimum log level", _syncMinimumLogLevel, f, "PortProSage:Sync:MinimumLogLevel",
            "How much detail gets written to the log files. Information is the normal, recommended setting - Debug " +
            "produces far more detail (useful when actively troubleshooting), Warning/Error/Fatal produce much less.\n\n" +
            "Example: Information logs each invoice processed and each write to Sage 50, without every low-level HTTP detail.",
            stretchInput: false);
        AddRow(grid, "Cleanup log after execution (Days)", _syncLogRetentionDays, f, "PortProSage:Sync:LogRetentionDays",
            "Checked at the end of every sync run (manual, automatic, or trigger) - any daily log file in Log folder " +
            "older than this many days is PERMANENTLY DELETED. This is NOT reversible; a removed log file cannot " +
            "be recovered.\n\n" +
            "0 means cleanup is turned OFF - nothing is ever removed automatically, regardless of how old the logs " +
            "get.\n\n" +
            "Example: 60 keeps the most recent 60 days of logs; anything older is deleted the next time any sync " +
            "runs (not on a fixed schedule of its own).");

        var applyCleanupNowButton = new Button
        {
            Text = "Apply Now",
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(3, 4, 3, 4)
        };
        applyCleanupNowButton.Click += (_, _) => ApplyLogCleanupNow();
        var applyCleanupRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "Clear History Now", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) }, 0, applyCleanupRow);
        AddWrappedWithHelp(grid, applyCleanupRow, applyCleanupNowButton, "Clear History Now",
            "Runs the log-retention cleanup right now, using whichever \"Cleanup log after execution (Days)\" " +
            "value is currently SAVED (save above first if you just changed it) - deletes any daily log file " +
            "older than that many days immediately, instead of waiting for it to happen automatically at the end " +
            "of the next sync run.\n\n" +
            "This is NOT reversible - a deleted log file cannot be recovered. If the saved value is 0, cleanup is " +
            "disabled and nothing will be deleted.");

        AddSectionHeading(grid, "Reset Imported-Invoice Tracking");

        AddRowWithButton(grid, "Currently tracked as already imported", _importedInvoiceCount, "(state database)", "imported_invoice",
            "How many PortPro invoices this app currently believes it already posted to Sage 50 - checked before " +
            "every invoice and used to silently skip it (\"ALREADY_IMPORTED\") WITHOUT re-verifying against Sage " +
            "50 itself. If you've switched to a different or fresh Sage 50 company file, this count still reflects " +
            "the OLD file, not the one currently in use.",
            "Refresh", (_, _) => RefreshImportedInvoiceCount(), inputWidth: 320);

        var clearImportedButton = new Button
        {
            Text = "Clear All Imported-Invoice Records",
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(3, 8, 3, 3)
        };
        clearImportedButton.Click += (_, _) => ClearImportedInvoiceTracking();
        var clearRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(clearImportedButton, 1, clearRow);

        var clearImportedNote = new Label
        {
            Text = "Only clear this if you're certain the Sage 50 company file currently in use does NOT already " +
                   "contain these invoices - otherwise they'll be posted again as duplicates on the next run.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(3, 2, 3, 8)
        };
        var noteRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(clearImportedNote, 0, noteRow);
        grid.SetColumnSpan(clearImportedNote, 3);

        var save = new Button { Text = "Save Settings" };
        save.Click += (_, _) => SaveSettingsTab();
        var saveBar = CreateActionButtonBar(save);

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(saveBar);

        RefreshAllTabsFromConfig += RefreshSettingsTab;
        return page;
    }

    private void RefreshSettingsTab()
    {
        if (_appSettings is null) return;

        _emailEnabled.Checked = _appSettings.GetBool("PortProSage.Email.Enabled");
        _emailSmtpHost.Text = _appSettings.GetString("PortProSage.Email.SmtpHost");
        _emailSmtpPort.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Email.SmtpPort", 587), _emailSmtpPort.Minimum, _emailSmtpPort.Maximum);
        _emailUseSsl.Checked = _appSettings.GetBool("PortProSage.Email.UseSsl", true);
        _emailFromAddress.Text = _appSettings.GetString("PortProSage.Email.FromAddress");
        _emailUsername.Text = _appSettings.GetString("PortProSage.Email.Username");
        _emailPassword.Text = _localSettings?.GetString("PortProSage.Email.Password") ?? "";
        _emailRecipients.Text = _appSettings.GetString("PortProSage.Email.RecipientAddressesCsv");

        _syncTriggerFolder.Text = _appSettings.GetString("PortProSage.Sync.TriggerFolder");
        _syncProcessedTriggerFolder.Text = _appSettings.GetString("PortProSage.Sync.ProcessedTriggerFolder");
        _syncStateDatabasePath.Text = _appSettings.GetString("PortProSage.Sync.StateDatabasePath");
        _syncLogFolder.Text = _appSettings.GetString("PortProSage.Sync.LogFolder");
        _syncFailedTransactionsFolder.Text = _appSettings.GetString("PortProSage.Sync.FailedTransactionsFolder");
        var level = _appSettings.GetString("PortProSage.Sync.MinimumLogLevel", "Information");
        _syncMinimumLogLevel.SelectedItem = _syncMinimumLogLevel.Items.Cast<string>().FirstOrDefault(i => i == level) ?? "Information";
        _syncLogRetentionDays.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Sync.LogRetentionDays", 0), _syncLogRetentionDays.Minimum, _syncLogRetentionDays.Maximum);

        // Also feeds the Run/Results tabs - they need the real TriggerFolder/
        // ProcessedTriggerFolder/LogFolder values, not a guess.
        RefreshRunTabFolders();
        RefreshImportedInvoiceCount();
    }

    private void RefreshImportedInvoiceCount()
    {
        var path = _syncStateDatabasePath.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            _importedInvoiceCount.Text = "(state database path not loaded yet)";
            return;
        }

        _importedInvoiceCount.Text = $"{ImportedInvoiceStateService.CountImported(path):N0} invoice(s)";
    }

    private void ClearImportedInvoiceTracking()
    {
        var path = _syncStateDatabasePath.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Load the Service config first (Settings tab) so the state database path is known.",
                "Not ready", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (runState, _) = GetServiceRunState();
        if (runState != ServiceRunState.NotRunning)
        {
            MessageBox.Show(this,
                "Something is currently running (the Automatic Service or a Manual Run) - stop it first. Clearing " +
                "this tracking while a sync is actively reading/writing the same state database could conflict " +
                "with it.",
                "Stop the running sync first", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var count = ImportedInvoiceStateService.CountImported(path);
        if (count == 0)
        {
            MessageBox.Show(this, "Nothing to clear - there are no imported-invoice records currently tracked.",
                "Nothing to do", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var message =
            $"WARNING: This will permanently clear all {count:N0} record(s) of which PortPro invoices this app " +
            "has already imported into Sage 50, AND run the log-retention cleanup now (deletes any daily log " +
            "file older than the currently saved \"Cleanup log after execution (Days)\" setting - see Clear " +
            "History Now above for what that does on its own).\n\n" +
            "The imported-invoice part does NOT touch Sage 50 itself - it only clears this app's own local " +
            "tracking. After this, EVERY invoice will be treated as brand new on the next run:\n\n" +
            "- If the Sage 50 company file currently in use genuinely does NOT already contain these invoices " +
            "(e.g. you're on a fresh/new company file), this is exactly what you want - they'll import normally.\n\n" +
            "- If Sage 50 ALREADY has some of them, they will be POSTED AGAIN AS DUPLICATES.\n\n" +
            "Only proceed if you're certain the current Sage 50 company file doesn't already contain these " +
            "invoices. Neither part of this can be undone.\n\n" +
            "Do you want to proceed?";

        var confirm = MessageBox.Show(this, message, "Confirm clear imported-invoice tracking",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        var removed = ImportedInvoiceStateService.ClearAll(path);
        RefreshImportedInvoiceCount();

        // Bundled with the reset, not just left for the next sync run to
        // trigger automatically - a fresh start for the imported-invoice
        // tracking is exactly the kind of moment old logs are no longer
        // relevant either.
        var deletedLogs = RunLogCleanup();

        MessageBox.Show(this,
            $"Cleared {removed:N0} imported-invoice record(s)." +
            (deletedLogs.Count > 0 ? $"\nAlso deleted {deletedLogs.Count:N0} old log file(s)." : ""),
            "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Deletes old log files using whichever LogFolder/LogRetentionDays
    /// are currently SAVED (not unsaved edits still sitting in the fields) -
    /// same "tests/acts on what's saved" convention as Test Connection. Returns
    /// the file names actually deleted.</summary>
    private List<string> RunLogCleanup()
    {
        if (_appSettings is null) return new List<string>();

        var logFolder = _appSettings.GetString("PortProSage.Sync.LogFolder");
        var retentionDays = _appSettings.GetInt("PortProSage.Sync.LogRetentionDays", 0);
        return LogCleanupService.CleanupOldLogs(logFolder, retentionDays);
    }

    private void ApplyLogCleanupNow()
    {
        if (_appSettings is null) return;

        var logFolder = _appSettings.GetString("PortProSage.Sync.LogFolder");
        var retentionDays = _appSettings.GetInt("PortProSage.Sync.LogRetentionDays", 0);

        if (retentionDays <= 0)
        {
            MessageBox.Show(this,
                "\"Cleanup log after execution (Days)\" is currently saved as 0 (disabled) - nothing will be " +
                "deleted. Set it to a positive number and Save Settings first if you want this to actually " +
                "remove old logs.",
                "Cleanup disabled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(logFolder) || !Directory.Exists(logFolder))
        {
            MessageBox.Show(this, $"Log folder not found:\n{logFolder}", "Not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var cutoff = DateTime.Today.AddDays(-retentionDays);
        var confirm = MessageBox.Show(this,
            $"This will PERMANENTLY delete every daily log file older than {cutoff:yyyy-MM-dd} ({retentionDays} " +
            $"day(s)) in:\n{logFolder}\n\nThis cannot be undone. Do you want to proceed?",
            "Confirm log cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        var deleted = LogCleanupService.CleanupOldLogs(logFolder, retentionDays);
        MessageBox.Show(this,
            deleted.Count > 0
                ? $"Deleted {deleted.Count:N0} log file(s) older than {retentionDays} day(s)."
                : "No log files were old enough to delete.",
            "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SaveSettingsTab()
    {
        if (_appSettings is null || _localSettings is null) return;

        _appSettings.SetBool("PortProSage.Email.Enabled", _emailEnabled.Checked);
        _appSettings.SetString("PortProSage.Email.SmtpHost", _emailSmtpHost.Text);
        _appSettings.SetInt("PortProSage.Email.SmtpPort", (int)_emailSmtpPort.Value);
        _appSettings.SetBool("PortProSage.Email.UseSsl", _emailUseSsl.Checked);
        _appSettings.SetString("PortProSage.Email.FromAddress", _emailFromAddress.Text);
        _appSettings.SetString("PortProSage.Email.Username", _emailUsername.Text);
        _appSettings.SetString("PortProSage.Email.RecipientAddressesCsv", _emailRecipients.Text);

        _appSettings.SetString("PortProSage.Sync.TriggerFolder", _syncTriggerFolder.Text);
        _appSettings.SetString("PortProSage.Sync.ProcessedTriggerFolder", _syncProcessedTriggerFolder.Text);
        _appSettings.SetString("PortProSage.Sync.StateDatabasePath", _syncStateDatabasePath.Text);
        _appSettings.SetString("PortProSage.Sync.LogFolder", _syncLogFolder.Text);
        _appSettings.SetString("PortProSage.Sync.FailedTransactionsFolder", _syncFailedTransactionsFolder.Text);
        _appSettings.SetString("PortProSage.Sync.MinimumLogLevel", _syncMinimumLogLevel.SelectedItem?.ToString() ?? "Information");
        _appSettings.SetInt("PortProSage.Sync.LogRetentionDays", (int)_syncLogRetentionDays.Value);
        _appSettings.Save();

        _localSettings.SetString("PortProSage.Email.Password", _emailPassword.Text);
        _localSettings.Save();

        RefreshRunTabFolders();
        MessageBox.Show(this, "Settings saved. The running Service needs a restart to pick up changes.", "Saved",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
