using System.Diagnostics;

namespace PortProSage.Admin;

public partial class MainForm
{
    private NumericUpDown _syncPollingIntervalMinutes = new() { Minimum = 1, Maximum = 1440 };
    private NumericUpDown _syncProcessingDelayDays = new() { Minimum = 0, Maximum = 3650 };

    // Live-computed, not persisted - purely a "what does this number actually
    // mean right now" readout next to the field itself, so you don't have to do
    // the today-minus-N math in your head. Recalculated on ValueChanged, which
    // fires for a typed value once you tab/click away or press Enter (same as
    // any arrow-key/spinner change).
    private Label _syncUpperCutoffDateLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(10, 8, 3, 3) };

    // Moved to the Settings tab's "Folder Locations" section (MainForm.SettingsTab.cs)
    // - these fields/AddFolderRow calls live there now, but AddFolderRow/
    // OpenInExplorer themselves stay here as shared helpers.
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

        AddProcessingDelayRow(grid, f);
        AddRow(grid, "Automatic Sync - Polling Interval (minutes)", _syncPollingIntervalMinutes, f, "PortProSage:Sync:PollingIntervalMinutes",
            "How often the automatic background poll checks PortPro for changed invoices, when the Service is running " +
            "continuously (not counting manual triggers, which are checked every 15 seconds regardless of this).\n\n" +
            "Example: 15 means PortPro is checked for new/changed invoices once every 15 minutes.");
        AddRow(grid, "Cutoff (Lower) Invoice Date", _syncCutoffInvoiceDate, f, "PortProSage:Sync:CutoffInvoiceDate",
            CutoffInvoiceDateHelpText, stretchInput: false);
        WireCutoffInvoiceDateControl(_syncCutoffInvoiceDate);
        RefreshAllTabsFromConfig += RefreshCutoffInvoiceDateControls;
        AddCheckRow(grid, _syncShowCommandWindow, f, "PortProSage:Sync:ShowCommandWindow", ShowCommandWindowHelpText);
        WireShowCommandWindowControl(_syncShowCommandWindow);
        RefreshAllTabsFromConfig += RefreshShowCommandWindowControls;
        AddCheckRow(grid, _syncSplitRunByDay, f, "PortProSage:Sync:SplitRunByDay", SplitRunByDayHelpText);
        WireSplitRunByDayControl(_syncSplitRunByDay);
        RefreshAllTabsFromConfig += RefreshSplitRunByDayControls;

        BuildPreviousRunSection(grid, _syncPrevRunMode, _syncPrevRunFrom, _syncPrevRunTo, _syncPrevRunMaxInvoices,
            _syncPrevRunFirstInvoiceProcessed, _syncPrevRunLastInvoiceProcessed);

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
    }

    private void SaveSyncTab()
    {
        if (_appSettings is null) return;
        _appSettings.SetInt("PortProSage.Sync.PollingIntervalMinutes", (int)_syncPollingIntervalMinutes.Value);
        _appSettings.SetInt("PortProSage.Sync.ProcessingDelayDays", (int)_syncProcessingDelayDays.Value);
        _appSettings.Save();

        MessageBox.Show(this, "Sync settings saved. The running Service needs a restart to pick up changes.", "Saved",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Like AddRow, but with a live "Upper cutoff date" readout wrapped in
    /// next to the NumericUpDown - today minus the entered number of days, so the
    /// actual date this many days holds back to is directly visible instead of
    /// needing to be worked out by hand. Recomputed on ValueChanged (fires once
    /// you tab/click away or press Enter after typing, or immediately for an
    /// arrow-key/spinner change).</summary>
    private void AddProcessingDelayRow(TableLayoutPanel grid, string fileName)
    {
        const string jsonPath = "PortProSage:Sync:ProcessingDelayDays";
        const string helpText =
            "Holds back the most recent N days - every watermark-driven run (the automatic poll, or a manual " +
            "\"Continue\" run) only processes invoices up to (today minus this many days), never anything more " +
            "recent, giving a just-changed invoice time to settle/be corrected in PortPro before it's synced to " +
            "Sage 50.\n\n" +
            "Example: if today is Aug 9 and this is 7, only invoices dated/changed up to Aug 2 are processed - " +
            "nothing from Aug 3 onward yet. Nothing is permanently skipped: an invoice held back this way is simply " +
            "picked up on a later run once it ages past the delay window.\n\n" +
            "Also used, once, on the very first automatic run ever (before any watermark exists) to set how far " +
            "back that first run's starting point is, counted from the same delayed upper bound above.\n\n" +
            "0 disables the delay - runs process up to right now, with no holdback. The \"Upper cutoff date\" shown " +
            "next to the field is today minus this number, recalculated live as soon as you change it.";

        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = "Automatic Sync - Processing Delay (Days)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 3)
        };

        _syncProcessingDelayDays.Anchor = AnchorStyles.Left;
        _syncProcessingDelayDays.Margin = new Padding(3, 4, 3, 4);

        var wrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        wrap.Controls.Add(_syncProcessingDelayDays);
        wrap.Controls.Add(_syncUpperCutoffDateLabel);
        wrap.Controls.Add(CreateHelpIcon(label.Text, helpText));

        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(wrap, 1, row);
        WireSource(_syncProcessingDelayDays, fileName, jsonPath);

        _syncProcessingDelayDays.ValueChanged += (_, _) => UpdateSyncUpperCutoffDateLabel();
        UpdateSyncUpperCutoffDateLabel();
    }

    private void UpdateSyncUpperCutoffDateLabel()
    {
        var days = (int)_syncProcessingDelayDays.Value;
        var cutoffDate = DateTime.Today.AddDays(-days);
        _syncUpperCutoffDateLabel.Text = $"Upper cutoff date: {cutoffDate:yyyy-MM-dd}" + (days == 0 ? " (today)" : $" (today - {days} day(s))");
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
