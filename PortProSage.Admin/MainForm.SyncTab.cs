using System.Diagnostics;
using PortProSage.Admin.Services;

namespace PortProSage.Admin;

public partial class MainForm
{
    private NumericUpDown _syncPollingIntervalMinutes = new() { Minimum = 1, Maximum = 1440 };
    private NumericUpDown _syncProcessingDelayDays = new() { Minimum = 0, Maximum = 3650 };

    // Single editable field, replacing the old separate Watermark tab (removed
    // 2026-08-25 - both the Automatic Service and Manual Run's watermark-driven
    // path only ever consume this one, and folding it into the top of the tab
    // that actually uses it is one less place to go looking. The old "Invoice #"
    // half is gone from the UI entirely - it was never a query bound (see
    // FilterType.LastChangedDate's doc comment), only a display/audit value, and
    // is still preserved untouched in the database by SaveSyncTab below even
    // though nothing here shows or edits it anymore. No checkbox (removed
    // 2026-08-25) - the watermark is always applicable and always a concrete
    // date; a brand new install with no run history yet gets a computed default
    // (see RefreshWatermarkDisplay) instead of an empty/cleared state. Saved as
    // part of "Save Automatic Sync settings" (SaveSyncTab), not its own button -
    // and disabled while the Automatic Service or a Manual Run is active (see
    // RefreshServiceStatus) so an edit can't be silently overwritten by a run
    // that's actively advancing this same value.
    private DateTimePicker _watermarkDate = new()
    {
        Width = 220,
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd HH:mm:ss"
    };

    private const string WatermarkDateHelpText =
        "The date the next Automatic Service cycle (or a Manual Run with \"Update Automatic Sync's starting " +
        "point\" checked) will continue from. Pick a date and click \"Save Automatic Sync settings\" below to set " +
        "it explicitly.\n\n" +
        "Must not be earlier than the Cutoff (Lower) Invoice Date below - saving a watermark before the cutoff is " +
        "rejected with an error, since the cutoff already guarantees nothing before it is ever processed anyway.\n\n" +
        "Unlike normal sync progress, which can only ever move this forward, this field bypasses that protection - " +
        "you can move it backward. Doing so will cause invoices in the newly-covered range to be re-fetched and " +
        "re-checked on the next run; already-imported invoices are tracked separately (by PortPro invoice id, not " +
        "by date) and will NOT be double-posted - only genuinely missed ones will actually import.\n\n" +
        "Disabled while the Automatic Service or a Manual Run is active - editing it mid-run risks the edit being " +
        "silently overwritten the moment that run next advances this same value.";

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

        AddWatermarkRow(grid);
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

        var save = new Button { Text = "Save Automatic Sync settings", Width = 190, Height = 36 };
        save.Click += (_, _) => SaveSyncTab();
        // Same accent-color treatment as Manual Run's Save button - see
        // CreateActionButtonBar - so it reads as a real action, not another gray
        // button indistinguishable from Start/Stop Automatic Service at a glance.
        save.BackColor = ActionButtonColor;
        save.ForeColor = Color.White;
        save.FlatStyle = FlatStyle.Flat;
        save.FlatAppearance.BorderSize = 0;
        save.Cursor = Cursors.Hand;

        WireServiceControlButtons();
        var automaticHelp = CreateHelpIcon("Automatic Sync", AutomaticServiceHelpText);

        // Start/Stop/Save at the bottom, same layout style as Manual Run's own
        // button panel (MainForm.RunTab.cs) - confirmed live 2026-08-25 the
        // operator wants the two tabs consistent, not Automatic Sync's controls
        // docked at the top while Manual Run's are at the bottom.
        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        _startServiceButton.Height = 36;
        _stopServiceButton.Height = 36;
        _startServiceButton.Location = new Point(12, 8);
        _stopServiceButton.Location = new Point(200, 8);
        save.Location = new Point(388, 8);
        automaticHelp.Location = new Point(590, 15);
        _serviceStatusLabel.Location = new Point(625, 15);
        _serviceStatusLabel.Font = new Font(_serviceStatusLabel.Font, FontStyle.Bold);
        buttonPanel.Controls.Add(_startServiceButton);
        buttonPanel.Controls.Add(_stopServiceButton);
        buttonPanel.Controls.Add(save);
        buttonPanel.Controls.Add(automaticHelp);
        buttonPanel.Controls.Add(_serviceStatusLabel);

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(buttonPanel);

        RefreshAllTabsFromConfig += RefreshSyncTab;
        RefreshAllTabsFromConfig += RefreshWatermarkDisplay;
        return page;
    }

    private void RefreshSyncTab()
    {
        if (_appSettings is null) return;
        _syncPollingIntervalMinutes.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Sync.PollingIntervalMinutes", 15), _syncPollingIntervalMinutes.Minimum, _syncPollingIntervalMinutes.Maximum);
        _syncProcessingDelayDays.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Sync.ProcessingDelayDays", 4), _syncProcessingDelayDays.Minimum, _syncProcessingDelayDays.Maximum);
    }

    /// <summary>Returns true if everything (including the watermark) genuinely
    /// saved, false if it was blocked (cutoff violation, or the watermark
    /// couldn't be saved at all) - callers that need settings to be current
    /// before proceeding (StartServiceProcess) check this instead of assuming a
    /// call always succeeds. showConfirmation=false suppresses the success
    /// pop-up (used when this is an implicit save-before-Start, not an explicit
    /// click of "Save Automatic Sync settings") - failure dialogs always show
    /// regardless, since silently failing to save before starting the service
    /// would be worse than the extra pop-up.</summary>
    private bool SaveSyncTab(bool showConfirmation = true)
    {
        if (_appSettings is null) return false;

        // The watermark can never be set earlier than the Cutoff (Lower) Invoice
        // Date - that cutoff already guarantees nothing before it is ever
        // processed, so a watermark behind it would just be silently unreachable
        // rather than genuinely meaningful. Checked before anything is saved, so
        // a rejected watermark doesn't leave the OTHER settings half-saved.
        if (_syncCutoffInvoiceDate.Checked && _watermarkDate.Value < _syncCutoffInvoiceDate.Value)
        {
            MessageBox.Show(this,
                $"Watermark Invoice Date ({_watermarkDate.Value:yyyy-MM-dd HH:mm:ss}) can't be earlier than the " +
                $"Cutoff (Lower) Invoice Date ({_syncCutoffInvoiceDate.Value:yyyy-MM-dd}) - nothing before the " +
                "cutoff is ever processed anyway. Adjust one of them before saving.",
                "Watermark before cutoff", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _appSettings.SetInt("PortProSage.Sync.PollingIntervalMinutes", (int)_syncPollingIntervalMinutes.Value);
        _appSettings.SetInt("PortProSage.Sync.ProcessingDelayDays", (int)_syncProcessingDelayDays.Value);
        _appSettings.Save();

        // Invoice # preserved exactly as it already was in the database - nothing
        // in the UI shows or edits it anymore (see _watermarkDate's doc comment),
        // but a tool reading it directly (e.g. --set-anchor) shouldn't have it
        // silently blanked out by a save that only ever touches the date here.
        // Scoped to the currently-SAVED Sage50 path (CurrentConfiguredSage50Path,
        // not any live-unsaved field) - matches Core's own per-path scoping, see
        // WatermarkStateService's doc comment for the bug this fixed. Reads
        // StateDatabasePath directly from _appSettings, not _syncStateDatabasePath.Text -
        // see RefreshWatermarkDisplay's doc comment for why that field can't be
        // trusted here either.
        var path = _appSettings.GetString("PortProSage.Sync.StateDatabasePath");
        var sage50Path = CurrentConfiguredSage50Path;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sage50Path))
        {
            MessageBox.Show(this,
                "Polling Interval and Processing Delay were saved, but the watermark was NOT - " +
                (string.IsNullOrWhiteSpace(path) ? "the state database path isn't known yet." : "no Sage 50 path is saved yet (go to the Sage 50 tab and Save first)."),
                "Watermark not saved", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var (_, currentInvoice) = WatermarkStateService.ReadCurrent(path, sage50Path);
        WatermarkStateService.WriteNew(path, sage50Path, _watermarkDate.Value, currentInvoice);

        if (showConfirmation)
        {
            MessageBox.Show(this, "Sync settings saved (including the watermark). The running Service needs a restart to pick up changes.",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        return true;
    }

    /// <summary>Custom row (not AddRow) since it needs its own Refresh + Save
    /// buttons together, not AddRow's single-button slot. Placed first in the
    /// tab - see BuildSyncTab - since it's the field every Automatic Sync cycle
    /// actually starts from.</summary>
    private void AddWatermarkRow(TableLayoutPanel grid)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = "Watermark Invoice Date",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 3)
        };

        _watermarkDate.Anchor = AnchorStyles.Left;
        _watermarkDate.Margin = new Padding(3, 4, 3, 4);

        var refreshButton = new Button { Text = "Refresh", Width = 70, Height = 23, Margin = new Padding(6, 5, 3, 3) };
        refreshButton.Click += (_, _) => RefreshWatermarkDisplay();

        var wrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        wrap.Controls.Add(_watermarkDate);
        wrap.Controls.Add(refreshButton);
        wrap.Controls.Add(CreateHelpIcon(label.Text, WatermarkDateHelpText));

        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(wrap, 1, row);
    }

    /// <summary>Reloads the live value from state.db, discarding any unsaved edit -
    /// no checkbox anymore (removed 2026-08-25), so a brand new install with no
    /// run history yet gets the same computed default Cutoff (Lower) Invoice Date
    /// uses (today - 6 months) rather than an empty/cleared field.
    ///
    /// Reads the state database path directly from _appSettings, NOT
    /// _syncStateDatabasePath.Text - confirmed live 2026-08-26 that field is only
    /// populated by RefreshSettingsTab (MainForm.SettingsTab.cs), and
    /// RefreshAllTabsFromConfig's subscribers fire in tab-construction order:
    /// this tab (Automatic Sync) is built well before Settings, so
    /// RefreshWatermarkDisplay was running - and reading that still-empty
    /// textbox - before RefreshSettingsTab ever got a chance to fill it in. Every
    /// config reload silently reset the watermark display to the 6-months-back
    /// default regardless of what was actually saved, and saving from that state
    /// (SaveSyncTab has the same fix) would have overwritten the real value with
    /// the wrong default.</summary>
    private void RefreshWatermarkDisplay()
    {
        var path = _appSettings?.GetString("PortProSage.Sync.StateDatabasePath");
        var sage50Path = CurrentConfiguredSage50Path;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sage50Path))
        {
            _watermarkDate.Value = DateTime.Today.AddMonths(-6);
            return;
        }

        var (date, _) = WatermarkStateService.ReadCurrent(path, sage50Path);
        _watermarkDate.Value = date?.ToLocalTime().DateTime ?? DateTime.Today.AddMonths(-6);
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
