using System.Diagnostics;

namespace PortProSage.Admin;

public partial class MainForm
{
    private NumericUpDown _syncPollingIntervalMinutes = new() { Minimum = 1, Maximum = 1440 };
    private NumericUpDown _syncProcessingDelayDays = new() { Minimum = 0, Maximum = 3650 };
    private TextBox _syncTriggerFolder = new();
    private TextBox _syncProcessedTriggerFolder = new();
    private TextBox _syncStateDatabasePath = new();
    private TextBox _syncLogFolder = new();
    private TextBox _syncFailedTransactionsFolder = new();
    private ComboBox _syncMinimumLogLevel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private NumericUpDown _syncLogRetentionDays = new() { Minimum = 0, Maximum = 3650 };

    private const int FieldHalfWidth = 420;

    private TabPage BuildSyncTab()
    {
        var page = new TabPage("Automatic Sync");
        var grid = NewFieldGrid();
        const string f = AppSettingsFileName;

        _syncMinimumLogLevel.Items.AddRange(new object[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" });

        AddRow(grid, "Automatic Sync - Processing Delay (Days)", _syncProcessingDelayDays, f, "PortProSage:Sync:ProcessingDelayDays",
            "Holds back the most recent N days - every watermark-driven run (the automatic poll, or a manual " +
            "\"Continue\" run) only processes invoices up to (today minus this many days), never anything more " +
            "recent, giving a just-changed invoice time to settle/be corrected in PortPro before it's synced to " +
            "Sage 50.\n\n" +
            "Example: if today is Aug 9 and this is 7, only invoices dated/changed up to Aug 2 are processed - " +
            "nothing from Aug 3 onward yet. Nothing is permanently skipped: an invoice held back this way is simply " +
            "picked up on a later run once it ages past the delay window.\n\n" +
            "Also used, once, on the very first automatic run ever (before any watermark exists) to set how far " +
            "back that first run's starting point is, counted from the same delayed upper bound above.\n\n" +
            "0 disables the delay - runs process up to right now, with no holdback.");
        AddRow(grid, "Automatic Sync - Polling Interval (minutes)", _syncPollingIntervalMinutes, f, "PortProSage:Sync:PollingIntervalMinutes",
            "How often the automatic background poll checks PortPro for changed invoices, when the Service is running " +
            "continuously (not counting manual triggers, which are checked every 15 seconds regardless of this).\n\n" +
            "Example: 15 means PortPro is checked for new/changed invoices once every 15 minutes.");
        AddRow(grid, "Cutoff (Lower) Invoice Date", _syncCutoffInvoiceDate, f, "PortProSage:Sync:CutoffInvoiceDate",
            CutoffInvoiceDateHelpText, stretchInput: false);
        WireCutoffInvoiceDateControl(_syncCutoffInvoiceDate);
        RefreshAllTabsFromConfig += RefreshCutoffInvoiceDateControls;
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
        AddCheckRow(grid, _syncShowCommandWindow, f, "PortProSage:Sync:ShowCommandWindow", ShowCommandWindowHelpText);
        WireShowCommandWindowControl(_syncShowCommandWindow);
        RefreshAllTabsFromConfig += RefreshShowCommandWindowControls;

        var save = new Button { Text = "Save Automatic Sync settings", Dock = DockStyle.Bottom, Height = 32 };
        save.Click += (_, _) => SaveSyncTab();

        var serviceControlPanel = BuildServiceControlPanel();

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(serviceControlPanel);
        page.Controls.Add(save);

        RefreshAllTabsFromConfig += RefreshSyncTab;
        return page;
    }

    private void RefreshSyncTab()
    {
        if (_appSettings is null) return;
        _syncPollingIntervalMinutes.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Sync.PollingIntervalMinutes", 15), _syncPollingIntervalMinutes.Minimum, _syncPollingIntervalMinutes.Maximum);
        _syncProcessingDelayDays.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Sync.ProcessingDelayDays", 7), _syncProcessingDelayDays.Minimum, _syncProcessingDelayDays.Maximum);
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

    private void SaveSyncTab()
    {
        if (_appSettings is null) return;
        _appSettings.SetInt("PortProSage.Sync.PollingIntervalMinutes", (int)_syncPollingIntervalMinutes.Value);
        _appSettings.SetInt("PortProSage.Sync.ProcessingDelayDays", (int)_syncProcessingDelayDays.Value);
        _appSettings.SetString("PortProSage.Sync.TriggerFolder", _syncTriggerFolder.Text);
        _appSettings.SetString("PortProSage.Sync.ProcessedTriggerFolder", _syncProcessedTriggerFolder.Text);
        _appSettings.SetString("PortProSage.Sync.StateDatabasePath", _syncStateDatabasePath.Text);
        _appSettings.SetString("PortProSage.Sync.LogFolder", _syncLogFolder.Text);
        _appSettings.SetString("PortProSage.Sync.FailedTransactionsFolder", _syncFailedTransactionsFolder.Text);
        _appSettings.SetString("PortProSage.Sync.MinimumLogLevel", _syncMinimumLogLevel.SelectedItem?.ToString() ?? "Information");
        _appSettings.SetInt("PortProSage.Sync.LogRetentionDays", (int)_syncLogRetentionDays.Value);
        _appSettings.Save();

        RefreshRunTabFolders();
        MessageBox.Show(this, "Sync settings saved. The running Service needs a restart to pick up changes.", "Saved",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Like AddRow, but for a folder/file-path field: the textbox sits at
    /// FieldHalfWidth (not full-stretch - a path doesn't need the whole form width)
    /// next to an "Open" button that jumps straight to that path in Explorer,
    /// instead of the user having to copy/paste it themselves. Built by hand rather
    /// than routed through AddRow because AddRow's WireSource wires the exact
    /// control passed in - wiring it to a wrapper panel instead of the textbox
    /// itself would silently break "click to see source" for these fields.</summary>
    private void AddFolderRow(TableLayoutPanel grid, string labelText, TextBox textBox, string fileName, string jsonPath,
        string helpText, bool isFile = false)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };

        textBox.Width = FieldHalfWidth;
        textBox.Anchor = AnchorStyles.Left;
        textBox.Margin = new Padding(3, 4, 3, 4);

        var openButton = new Button { Text = "Open", Width = 60, Height = 23, Margin = new Padding(6, 5, 3, 3) };
        openButton.Click += (_, _) => OpenInExplorer(textBox.Text, isFile);

        var wrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        wrap.Controls.Add(textBox);
        wrap.Controls.Add(openButton);
        if (!string.IsNullOrEmpty(helpText))
        {
            // Wrapped together with the textbox+button, not column 2's fixed
            // far-right position - so the icon sits right next to the button
            // instead of stranded past a large empty gap.
            wrap.Controls.Add(CreateHelpIcon(labelText.Replace("\n", " "), helpText));
        }

        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(wrap, 1, row);
        WireSource(textBox, fileName, jsonPath);
    }

    private void OpenInExplorer(string path, bool isFile)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "This field is empty.", "Nothing to open", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            if (isFile && File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                return;
            }

            var folder = isFile ? Path.GetDirectoryName(path) : path;
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start("explorer.exe", $"\"{folder}\"");
            }
            else
            {
                MessageBox.Show(this, $"Doesn't exist yet on this machine:\n{path}", "Not found",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open:\n{path}\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
