using PortProSage.Admin.Services;

namespace PortProSage.Admin;

public partial class MainForm
{
    // Read-only - reflects whatever is actually in state.db right now, not a
    // cached/stale value - RefreshCurrentWatermarkDisplay() re-reads the file
    // every time it's called (tab shown, config (re)loaded, or right after Save).
    private TextBox _currentWatermarkDate = new() { ReadOnly = true, Enabled = false, Width = 320 };
    private TextBox _currentWatermarkInvoice = new() { ReadOnly = true, Enabled = false, Width = 320 };

    // Checked = set the date watermark to this value; unchecked = clear it
    // entirely (same "as if no run has ever happened" state a brand new
    // state.db would be in). Invoice # is a plain optional textbox - blank
    // clears it, same idea.
    private DateTimePicker _newWatermarkDate = new() { ShowCheckBox = true, Checked = false, Width = 220 };
    private TextBox _newWatermarkInvoice = new() { Width = 220 };

    private const string NewWatermarkDateHelpText =
        "The date the NEXT \"Continue\" run (manual or automatic) will start from. Check the box and pick a date " +
        "to set it explicitly - unchecked (the default) means CLEAR it entirely, which makes the next run behave " +
        "exactly as if no sync has ever completed, starting from (delayed upper bound - Processing Delay Days), " +
        "same as a brand new install.\n\n" +
        "Unlike the normal sync, which can only ever move this forward, this bypasses that protection - you can " +
        "move it backward. Doing so will cause invoices in the newly-covered range to be re-fetched and re-checked " +
        "on the next run; already-imported invoices are tracked separately (by PortPro invoice id, not by date) " +
        "and will NOT be double-posted - only genuinely missed ones will actually import.";

    private const string NewWatermarkInvoiceHelpText =
        "Display/audit value only, shown in the Previous Run section - it does NOT itself drive what gets fetched " +
        "(the date above does). Leave blank to clear it.\n\nExample: RSRE_000849";

    private TabPage BuildWatermarkTab()
    {
        var page = new TabPage("Watermark");
        var grid = NewFieldGrid();

        AddSectionHeading(grid, "Current Watermark",
            "The persisted \"continue from where we left off\" position used by Continue mode and the Automatic " +
            "Service - read directly from the state database (not appsettings.json).");

        AddRow(grid, "Current Watermark Date", _currentWatermarkDate, "(state database)", "watermark.last_changed_date",
            stretchInput: false);
        AddRowWithButton(grid, "Current Watermark Invoice #", _currentWatermarkInvoice, "(state database)", "watermark.last_processed_invoice_number",
            "", "Refresh", (_, _) => RefreshCurrentWatermarkDisplay(), inputWidth: 320);

        AddSectionHeading(grid, "Reset / Change Watermark",
            "Directly overrides the state database - use this to unstick a watermark that's gotten ahead of a " +
            "raised Processing Delay Days, or to deliberately re-scan an earlier range. Starts as a copy of the " +
            "current watermark above - edit only what you need to change. Saving asks for confirmation first, " +
            "since the current value cannot be recovered once overwritten.");

        AddRow(grid, "New Watermark Date", _newWatermarkDate, "(state database - not a settings file)", "watermark.last_changed_date",
            NewWatermarkDateHelpText, stretchInput: false);
        AddRow(grid, "New Watermark Invoice #", _newWatermarkInvoice, "(state database - not a settings file)", "watermark.last_processed_invoice_number",
            NewWatermarkInvoiceHelpText, stretchInput: false);

        // Compact (Top + AutoSize, not Fill) so the tab's content hugs its own
        // natural height instead of stretching across the whole window - the
        // bottom panel below picks up whatever space this doesn't need, rather
        // than leaving a dead gap between the fields and the Save button.
        var fieldsScroll = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        fieldsScroll.Controls.Add(grid);

        var saveButton = new Button
        {
            Text = "Save (Reset/Change Watermark)",
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(3, 12, 3, 3)
        };
        saveButton.Click += (_, _) => SaveWatermarkChange();

        var note = new Label
        {
            Text = "This changes the SAME state database the Automatic Service and Manual Run \"Continue\" mode " +
                   "read from - stop both first (see the header bar) before saving a change here.",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 3, 3, 3)
        };

        // FlowLayoutPanel (TopDown), not raw Dock=Top stacking, for the note+
        // button pair - sidesteps WinForms' well-known "last-docked-control-
        // ends-up-on-top" gotcha (same reasoning as the Previous Run section's
        // heading rows), so they render in the order added: note, then button.
        var bottomStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(9, 8, 3, 3)
        };
        bottomStack.Controls.Add(note);
        bottomStack.Controls.Add(saveButton);

        var bottomPanel = new Panel { Dock = DockStyle.Fill };
        bottomPanel.Controls.Add(bottomStack);

        page.Controls.Add(bottomPanel);
        page.Controls.Add(fieldsScroll);

        RefreshAllTabsFromConfig += RefreshCurrentWatermarkDisplay;
        return page;
    }

    /// <summary>Adds a bold heading + gray subtext row pair - same visual pattern
    /// as the Previous Run section's heading (MainForm.RunTab.cs), used here for
    /// the two sub-sections of this tab. Margins match a normal AddRow row's
    /// (top 8/bottom 3-4), not an oversized custom gap - see the standard
    /// row rhythm used throughout the rest of the app (e.g. the PortPro tab).</summary>
    private static void AddSectionHeading(TableLayoutPanel grid, string heading, string subText)
    {
        var headingRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var headingLabel = new Label
        {
            Text = heading,
            AutoSize = true,
            Font = new Font(grid.Font, FontStyle.Bold),
            Margin = new Padding(3, 8, 3, 2)
        };
        grid.Controls.Add(headingLabel, 0, headingRow);
        grid.SetColumnSpan(headingLabel, 3);

        var subRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var subLabel = new Label
        {
            Text = subText,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 4),
            MaximumSize = new Size(700, 0)
        };
        grid.Controls.Add(subLabel, 0, subRow);
        grid.SetColumnSpan(subLabel, 3);
    }

    private void RefreshCurrentWatermarkDisplay()
    {
        var path = _syncStateDatabasePath.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            _currentWatermarkDate.Text = "(state database path not loaded yet - load config on the Automatic Sync tab)";
            _currentWatermarkInvoice.Text = "";
            return;
        }

        var (date, invoiceNumber) = WatermarkStateService.ReadCurrent(path);
        _currentWatermarkDate.Text = date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "(none - no run has ever completed)";
        _currentWatermarkInvoice.Text = invoiceNumber ?? "(none)";

        // "New Watermark" defaults to a copy of "Current Watermark", not blank -
        // so it reads as an editable starting point rather than requiring every
        // field to be re-entered from scratch just to nudge one value.
        if (date is not null)
        {
            _newWatermarkDate.Checked = true;
            _newWatermarkDate.Value = date.Value.ToLocalTime().DateTime;
        }
        else
        {
            _newWatermarkDate.Checked = false;
        }
        _newWatermarkInvoice.Text = invoiceNumber ?? "";
    }

    private void SaveWatermarkChange()
    {
        var path = _syncStateDatabasePath.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "Load the Service config first (Automatic Sync tab) so the state database path is known.",
                "Not ready", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (runState, _) = GetServiceRunState();
        if (runState != ServiceRunState.NotRunning)
        {
            MessageBox.Show(this,
                "Something is currently running (the Automatic Service or a Manual Run) - stop it first. Changing " +
                "the watermark while a sync is actively reading/writing the same state database could conflict " +
                "with it or be silently overwritten.",
                "Stop the running sync first", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (currentDate, currentInvoice) = WatermarkStateService.ReadCurrent(path);
        DateTimeOffset? newDate = _newWatermarkDate.Checked ? _newWatermarkDate.Value : null;
        var newInvoice = string.IsNullOrWhiteSpace(_newWatermarkInvoice.Text) ? null : _newWatermarkInvoice.Text.Trim();

        var message =
            "WARNING: You are about to change the persisted sync watermark used by \"Continue\" runs and the " +
            "Automatic Service.\n\n" +
            $"Current watermark date:      {currentDate?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "(none)"}\n" +
            $"Current watermark invoice #: {currentInvoice ?? "(none)"}\n\n" +
            $"New watermark date:      {(newDate is not null ? newDate.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "(cleared - none)")}\n" +
            $"New watermark invoice #: {newInvoice ?? "(cleared - none)"}\n\n" +
            "You will LOSE the current watermark information - it CANNOT be recovered once this is saved, " +
            "overwritten, or cleared.\n\n" +
            "The next Continue/Automatic run will use the new value - or, if cleared, will treat this as if no " +
            "sync has ever run at all, starting from the Processing Delay Days setting.\n\n" +
            "Do you want to proceed?";

        var confirm = MessageBox.Show(this, message, "Confirm watermark change",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        WatermarkStateService.WriteNew(path, newDate, newInvoice);
        RefreshCurrentWatermarkDisplay();

        MessageBox.Show(this, "Watermark updated.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
