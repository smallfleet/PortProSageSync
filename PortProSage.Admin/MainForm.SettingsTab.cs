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

        var save = new Button { Text = "Save Settings", Dock = DockStyle.Bottom, Height = 32 };
        save.Click += (_, _) => SaveSettingsTab();

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(save);

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
