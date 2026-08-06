namespace PortProSage.Admin;

public partial class MainForm
{
    private NumericUpDown _syncPollingIntervalMinutes = new() { Minimum = 1, Maximum = 1440 };
    private NumericUpDown _syncInitialLookbackDays = new() { Minimum = 1, Maximum = 3650 };
    private TextBox _syncTriggerFolder = new();
    private TextBox _syncProcessedTriggerFolder = new();
    private TextBox _syncStateDatabasePath = new();
    private TextBox _syncLogFolder = new();
    private TextBox _syncFailedTransactionsFolder = new();
    private ComboBox _syncMinimumLogLevel = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    private TabPage BuildSyncTab()
    {
        var page = new TabPage("Sync");
        var grid = NewFieldGrid();
        const string f = AppSettingsFileName;

        _syncMinimumLogLevel.Items.AddRange(new object[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" });

        AddRow(grid, "Polling interval (minutes)", _syncPollingIntervalMinutes, f, "PortProSage:Sync:PollingIntervalMinutes",
            "How often the automatic background poll checks PortPro for changed invoices, when the Service is running " +
            "continuously (not counting manual triggers, which are checked every 15 seconds regardless of this).\n\n" +
            "Example: 15 means PortPro is checked for new/changed invoices once every 15 minutes.");
        AddRow(grid, "Initial lookback (days)", _syncInitialLookbackDays, f, "PortProSage:Sync:InitialLookbackDays",
            "Only matters the very first time the automatic poll ever runs, before any watermark has been saved yet. " +
            "It sets how far back to look for changed invoices on that first run.\n\n" +
            "Example: 7 means the very first automatic poll looks at invoices changed in the last 7 days. After that " +
            "first run, it always continues from the saved watermark instead, regardless of this value.");
        AddRow(grid, "Trigger folder", _syncTriggerFolder, f, "PortProSage:Sync:TriggerFolder",
            "The folder the running Service watches for new manual sync requests - both the command-line Trigger " +
            "tool and this Admin app's Run tab drop a request file here.\n\n" +
            "Example: C:\\PortProSageSync\\requests");
        AddRow(grid, "Processed trigger folder", _syncProcessedTriggerFolder, f, "PortProSage:Sync:ProcessedTriggerFolder",
            "Where a request file (and its result) get moved once the Service has finished processing it. The " +
            "History & Logs tab reads completed runs from here.\n\n" +
            "Example: C:\\PortProSageSync\\requests\\processed");
        AddRow(grid, "State database path", _syncStateDatabasePath, f, "PortProSage:Sync:StateDatabasePath",
            "The SQLite database file that remembers which PortPro invoices have already been imported, and the " +
            "watermark used by \"continue from where we left off\". Do not point two different client deployments " +
            "at the same file.\n\nExample: C:\\PortProSageSync\\state.db");
        AddRow(grid, "Log folder", _syncLogFolder, f, "PortProSage:Sync:LogFolder",
            "Where the Service writes its daily rolling log files (one file per day, kept for 30 days). The " +
            "History & Logs tab reads these to show each run's full log.\n\nExample: C:\\PortProSageSync\\logs");
        AddRow(grid, "Failed transactions folder", _syncFailedTransactionsFolder, f, "PortProSage:Sync:FailedTransactionsFolder",
            "Where a CSV report of any failed invoices from a run gets saved, with a microsecond-precision timestamp " +
            "in the filename. Written every time a run has at least one failure, regardless of whether email is enabled.\n\n" +
            "Example: C:\\PortProSageSync\\failed-transactions");
        AddRow(grid, "Minimum log level", _syncMinimumLogLevel, f, "PortProSage:Sync:MinimumLogLevel",
            "How much detail gets written to the log files. Information is the normal, recommended setting - Debug " +
            "produces far more detail (useful when actively troubleshooting), Warning/Error/Fatal produce much less.\n\n" +
            "Example: Information logs each invoice processed and each write to Sage 50, without every low-level HTTP detail.");

        var save = new Button { Text = "Save Sync settings", Dock = DockStyle.Bottom, Height = 32 };
        save.Click += (_, _) => SaveSyncTab();

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(save);

        RefreshAllTabsFromConfig += RefreshSyncTab;
        return page;
    }

    private void RefreshSyncTab()
    {
        if (_appSettings is null) return;
        _syncPollingIntervalMinutes.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Sync.PollingIntervalMinutes", 15), _syncPollingIntervalMinutes.Minimum, _syncPollingIntervalMinutes.Maximum);
        _syncInitialLookbackDays.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Sync.InitialLookbackDays", 7), _syncInitialLookbackDays.Minimum, _syncInitialLookbackDays.Maximum);
        _syncTriggerFolder.Text = _appSettings.GetString("PortProSage.Sync.TriggerFolder");
        _syncProcessedTriggerFolder.Text = _appSettings.GetString("PortProSage.Sync.ProcessedTriggerFolder");
        _syncStateDatabasePath.Text = _appSettings.GetString("PortProSage.Sync.StateDatabasePath");
        _syncLogFolder.Text = _appSettings.GetString("PortProSage.Sync.LogFolder");
        _syncFailedTransactionsFolder.Text = _appSettings.GetString("PortProSage.Sync.FailedTransactionsFolder");
        var level = _appSettings.GetString("PortProSage.Sync.MinimumLogLevel", "Information");
        _syncMinimumLogLevel.SelectedItem = _syncMinimumLogLevel.Items.Cast<string>().FirstOrDefault(i => i == level) ?? "Information";

        // Also feeds the Run/Results tabs - they need the real TriggerFolder/
        // ProcessedTriggerFolder/LogFolder values, not a guess.
        RefreshRunTabFolders();
    }

    private void SaveSyncTab()
    {
        if (_appSettings is null) return;
        _appSettings.SetInt("PortProSage.Sync.PollingIntervalMinutes", (int)_syncPollingIntervalMinutes.Value);
        _appSettings.SetInt("PortProSage.Sync.InitialLookbackDays", (int)_syncInitialLookbackDays.Value);
        _appSettings.SetString("PortProSage.Sync.TriggerFolder", _syncTriggerFolder.Text);
        _appSettings.SetString("PortProSage.Sync.ProcessedTriggerFolder", _syncProcessedTriggerFolder.Text);
        _appSettings.SetString("PortProSage.Sync.StateDatabasePath", _syncStateDatabasePath.Text);
        _appSettings.SetString("PortProSage.Sync.LogFolder", _syncLogFolder.Text);
        _appSettings.SetString("PortProSage.Sync.FailedTransactionsFolder", _syncFailedTransactionsFolder.Text);
        _appSettings.SetString("PortProSage.Sync.MinimumLogLevel", _syncMinimumLogLevel.SelectedItem?.ToString() ?? "Information");
        _appSettings.Save();

        RefreshRunTabFolders();
        MessageBox.Show(this, "Sync settings saved. The running Service needs a restart to pick up changes.", "Saved",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
