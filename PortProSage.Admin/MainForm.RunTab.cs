using System.Diagnostics;
using System.Text.Json.Nodes;
using PortProSage.Admin.Models;
using PortProSage.Admin.Services;

namespace PortProSage.Admin;

public partial class MainForm
{
    private string _triggerFolder = "";
    private string _processedTriggerFolder = "";
    private string _logFolder = "";
    private string _manualRunFolder = "";
    private string _autoPollFolder = "";
    private Process? _manualRunProcess;

    // Fraction of the primary screen's width, not a fixed pixel guess - falls back
    // to 1920px if Screen.PrimaryScreen is ever unavailable (e.g. headless test run).
    // The Invoice number list field is NOT sized this way (see UpdateInvoiceNumberListWidth) -
    // it tracks the actual window's width live, not a one-time screen-based guess.
    private static readonly int RunModeWidth = (int)((Screen.PrimaryScreen?.Bounds.Width ?? 1920) * 0.25);

    private ComboBox _runMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = RunModeWidth };
    private DateTimePicker _runFrom = new() { Width = 220 };
    private DateTimePicker _runTo = new() { Width = 220 };
    private TextBox _runStartInvoice = new() { Width = 160 };
    private TextBox _runEndInvoice = new() { Width = 160 };
    private TextBox _runInvoiceNumberList = new() { Width = 400 }; // real width set live by UpdateInvoiceNumberListWidth
    private CheckBox _runOverrideAlreadyImported = new() { Text = "Override \"Already Imported\" check for this run", AutoSize = true };

    // Only relevant for Invoice date mode - see AdvanceWatermarkHelpText. Defaults
    // CHECKED (confirmed live 2026-08-25); never persisted, so it can't silently
    // carry a stale choice into an unrelated later run - see UpdateRunModeFieldStates.
    private CheckBox _runAdvanceWatermark = new()
    {
        Text = "Update Automatic Sync's starting point to this run's End date (not saved)",
        AutoSize = true
    };
    private NumericUpDown _runMaxInvoices = new() { Minimum = 0, Maximum = 100000, Width = 120 };
    private CheckBox _runDryRun = new() { Text = "Dry run (Simulated - Default 10 Invoices and no real Sage 50 Changes)", AutoSize = true };
    private Button _manualRunButton = new() { Text = "Manual Run", Width = 140, Height = 36 };
    private Button _manualRunStopButton = new() { Text = "Stop Manual Run", Width = 140, Height = 36, Enabled = false };
    private Button _manualRunSaveButton = new() { Text = "Save", Width = 90, Height = 36 };

    // "Previous Run" section - a read-only snapshot of the most recently completed
    // run's parameters, so it's directly visible (not just documented) that
    // whichever mode was actually used, its values are retained/recorded rather
    // than lost. Refreshed by RefreshPreviousRunSection(), called every time
    // RefreshHistoryList() runs (MainForm.HistoryTab.cs) - initial load, after
    // starting/stopping a Manual Run, and on the result-poll timer.
    private TextBox _prevRunMode = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _prevRunFrom = new() { ReadOnly = true, Enabled = false, Width = 220 };
    private TextBox _prevRunTo = new() { ReadOnly = true, Enabled = false, Width = 220 };
    private TextBox _prevRunMaxInvoices = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _prevRunFirstInvoiceProcessed = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _prevRunLastInvoiceProcessed = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _prevRunResult = new() { ReadOnly = true, Enabled = false, Width = 650 };
    private TextBox _prevRunInvoiceListUsed = new() { ReadOnly = true, Enabled = false, Width = 650 };

    // Same "Previous Run" data, shown a second time on the Automatic Sync tab -
    // it's not just a Manual Run concern, the automatic poll's most recent
    // outcome is exactly as relevant there. Kept as a separate set of controls
    // (not the same instances reused on two tabs, which WinForms doesn't allow -
    // a control can only ever live under one parent) and refreshed in lockstep
    // by RefreshPreviousRunSection.
    private TextBox _syncPrevRunMode = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _syncPrevRunFrom = new() { ReadOnly = true, Enabled = false, Width = 220 };
    private TextBox _syncPrevRunTo = new() { ReadOnly = true, Enabled = false, Width = 220 };
    private TextBox _syncPrevRunMaxInvoices = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _syncPrevRunFirstInvoiceProcessed = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _syncPrevRunLastInvoiceProcessed = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _syncPrevRunResult = new() { ReadOnly = true, Enabled = false, Width = 650 };
    private TextBox _syncPrevRunInvoiceListUsed = new() { ReadOnly = true, Enabled = false, Width = 650 };

    private const string ManualRunHelpText =
        "Runs the sync ONE TIME, right now, in its own dedicated process - it does not write a file for something " +
        "else to notice, and it does not keep running afterward like the Automatic Service does. This is the " +
        "equivalent of running PortProSage.Service.exe --run-once yourself from the command line.\n\n" +
        "Disabled while the Automatic Service is running, and starting a Manual Run disables the Automatic Service " +
        "Start button in turn - both would otherwise try to open Sage 50 under the same configured username at the " +
        "same time, which Sage 50 rejects as a second simultaneous session.\n\n" +
        "Use \"Stop Manual Run\" to interrupt it if it's taking too long or picked up more than intended - it sends " +
        "a graceful shutdown signal first (same as Ctrl+C, so already-imported invoices and the last-processed " +
        "anchor stay correctly recorded up to that point), falling back to a hard stop only if it doesn't respond.";

    private const string OverrideAlreadyImportedHelpText =
        "Normally, an invoice this app already recorded as imported is silently skipped on every later run - that's " +
        "what stops the same invoice from being posted to Sage 50 twice. Checking this box turns that skip off for " +
        "THIS RUN ONLY: a previously-imported invoice is re-validated and re-posted instead.\n\n" +
        "This is NEVER saved anywhere - it always starts unchecked when the app opens, and resets back to unchecked " +
        "itself as soon as this run finishes or is stopped, so it can't silently carry forward into an unrelated " +
        "later run.\n\n" +
        "⚠ If the invoice is genuinely still in Sage 50, re-posting it creates a real duplicate - this does not " +
        "remove or replace the original. Only use this after confirming (in Sage 50 itself) that the invoice(s) " +
        "covered by the selected mode actually need to go in again.";

    // Added 2026-08-25 alongside removing "Continue"/"Last changed date" modes -
    // Invoice date's own date field (PortPro's billingDate) is unrelated to what
    // the watermark actually tracks (PortPro's "last changed" timestamp - see
    // FilterType.LastChangedDate's doc comment), so promoting it isn't automatic;
    // this is an explicit, opt-in choice for when the operator genuinely knows
    // this run represents "we're caught up through this date."
    private const string AdvanceWatermarkHelpText =
        "When checked, this run also advances the Automatic Service's saved starting point (the watermark) from " +
        "what it actually processed - the same thing a watermark-driven run does, just without also letting the " +
        "watermark dictate the range (this run's From/To are used exactly as entered either way).\n\n" +
        "Only ever updates the DATE half of the watermark, from each processed invoice's own real \"last changed\" " +
        "timestamp - not simply this run's End date, and never the invoice-number half, which isn't derivable from " +
        "a date range at all.\n\n" +
        "Checked by default for Invoice date mode. Uncheck it for a one-off range that shouldn't move the " +
        "Automatic Service's position - leaving the watermark untouched just means it will later re-check this " +
        "same window on its own and skip everything as already-imported, which is harmless but redundant.\n\n" +
        "This is NEVER saved - it resets to its default every time this tab loads or Mode changes, so it can't " +
        "silently carry forward into an unrelated later run.";

    private TabPage BuildRunTab()
    {
        var page = new TabPage("Manual Run");
        var grid = NewFieldGrid();

        _runMode.Items.AddRange(new object[]
        {
            "Invoice date",
            "Invoice number range",
            "Invoice number list (comma-separated)"
        });
        // "Continue (from where we left off)" and "Last changed date" removed
        // 2026-08-25 - Continue was mechanically identical to the Automatic
        // Service's own poll cycle (same FilterType.LastChangedDate + UseWatermark
        // combination), so there was no real reason to duplicate it here; Last
        // changed date (an explicit range on that same "last touched" field,
        // decoupled from the watermark) was rarely if ever used. See the new
        // "Update Automatic Sync's starting point" checkbox below for how Invoice
        // date mode can now optionally advance the watermark instead.
        _runMode.SelectedIndex = 0;
        _runMode.SelectedIndexChanged += (_, _) => UpdateRunModeFieldStates();

        AddRow(grid, "Mode", _runMode, "(request - not a settings file)", "SyncRequest.FilterType",
            "Picks how invoices get selected for this one run:\n\n" +
            "• Invoice date (default) - invoices whose own date (PortPro's billingDate) falls in the From/To " +
            "window below. This is what you almost always want for a specific date range.\n" +
            "• Invoice number range - invoices whose reference number falls between Start/End invoice number below, " +
            "with BOTH endpoints included (e.g. Start=90, End=95 processes 90, 91, 92, 93, 94, 95 - 6 invoices, not 5). " +
            "Uses PortPro's paginated list endpoint, scanning the whole account.\n" +
            "• Invoice number list - an explicit, comma-separated set of specific invoice numbers (see the field " +
            "below), fetched ONE AT A TIME via PortPro's single-invoice lookup instead of the list endpoint.\n\n" +
            "Every run, regardless of mode, is automatically followed by a gap-fill sweep of the exact invoice-" +
            "number range it actually touched - confirmed live 2026-08-12 the list endpoint can silently miss real " +
            "invoices; this catches them without needing a separate mode. It shows up as its own row in History & " +
            "Logs, and never needs choosing by hand.\n\n" +
            "None of these modes touch the Automatic Service's saved position (the watermark) unless you " +
            "explicitly check \"Update Automatic Sync's starting point\" for Invoice date mode - see that " +
            "checkbox's own help for why.",
            stretchInput: false);
        AddRow(grid, "Invoice Date From", _runFrom, "(request)", "SyncRequest.From",
            "Start of the date window - only used by Invoice date mode.\n\n" +
            "Example: set From to 2026-07-01 and To to 2026-07-31 to process everything from July 2026.",
            stretchInput: false);
        AddRow(grid, "Invoice Date To", _runTo, "(request)", "SyncRequest.To",
            "End of the date window - only used by Invoice date mode.\n\n" +
            "Example: set From to 2026-07-01 and To to 2026-07-31 to process everything from July 2026.",
            stretchInput: false);
        AddCheckRow(grid, _runAdvanceWatermark, "(request - not saved)", "SyncRequest.AdvanceWatermarkOnCompletion",
            AdvanceWatermarkHelpText);
        AddRow(grid, "Cutoff (Lower) Invoice Date", _runCutoffInvoiceDate, "(request - not a settings file)", "PortProSage:Sync:CutoffInvoiceDate",
            CutoffInvoiceDateHelpText, stretchInput: false);
        WireCutoffInvoiceDateControl(_runCutoffInvoiceDate);
        AddRow(grid, "Start invoice number", _runStartInvoice, "(request)", "SyncRequest.StartInvoiceNumber",
            "The lowest PortPro reference number to include - only used by Invoice number range mode. Leave blank " +
            "for no lower bound.\n\nExample: RSRE_000102",
            stretchInput: false);
        AddRow(grid, "End invoice number", _runEndInvoice, "(request)", "SyncRequest.EndInvoiceNumber",
            "The highest PortPro reference number to include - only used by Invoice number range mode. Leave blank " +
            "for no upper bound.\n\nExample: Start=90, End=95 processes 90, 91, 92, 93, 94, 95 - 6 invoices (both " +
            "ends included).",
            stretchInput: false);
        AddRow(grid, "Invoice number list (comma-separated)", _runInvoiceNumberList, "(request)", "SyncRequest.InvoiceNumberList",
            "Only used by Invoice number list mode - an explicit set of specific invoice numbers, separated by " +
            "commas. Each one is fetched directly via PortPro's single-invoice endpoint, not the paginated list " +
            "endpoint Invoice number range uses - so this reliably finds a specific invoice even in the rare case " +
            "the list endpoint doesn't return it.\n\n" +
            "Example: RSRE_000284, RSRE_000301, RSRE_000455",
            stretchInput: false);
        AddCheckRow(grid, _runOverrideAlreadyImported, "(not saved anywhere - always resets to unchecked)",
            "SyncRequest.OverrideAlreadyImportedCheck (one-time, this run only)", OverrideAlreadyImportedHelpText);
        AddRow(grid, "Max invoices to process (0 = no limit)", _runMaxInvoices, "(request)", "SyncRequest.MaxInvoicesToProcess",
            "Caps how many eligible (amount > 0) invoices this run actually processes, on top of whatever Mode " +
            "selects - once this many have been GENUINELY PROCESSED, the run stops even if more would otherwise " +
            "qualify. 0 means no cap.\n\n" +
            "\"Processed\" means imported (or, under Dry Run, simulated as imported - see the Dry run checkbox " +
            "below) or failed - an invoice already recorded as imported previously does NOT count against this " +
            "cap, since nothing was actually done for it. Example: Max = 10 with 5 of the next invoices already " +
            "imported and 10 genuinely new ones processes all 10 new ones (15 total looked at, not stopping at " +
            "invoice #10 overall) - the cap tracks real work, not how many invoices were glanced at.\n\n" +
            "Example: Invoice date mode covering a month with Max invoices = 10 processes only the first 10 " +
            "genuinely new invoices in that window, even if 50 qualify.");
        AddCheckRow(grid, _runShowCommandWindow, "(request - not a settings file)", "PortProSage:Sync:ShowCommandWindow", ShowCommandWindowHelpText);
        WireShowCommandWindowControl(_runShowCommandWindow);

        AddCheckRow(grid, _runDryRun, "(request - not a settings file)", "PortProSage:Sage50:DryRun", RunDryRunHelpText);
        WireDryRunControl(_runDryRun);

        BuildPreviousRunSection(grid, _prevRunMode, _prevRunFrom, _prevRunTo, _prevRunMaxInvoices,
            _prevRunFirstInvoiceProcessed, _prevRunLastInvoiceProcessed, _prevRunResult, _prevRunInvoiceListUsed);

        _manualRunButton.Click += (_, _) => StartManualRun();
        _manualRunStopButton.Click += (_, _) => StopManualRun();
        _manualRunSaveButton.Click += (_, _) =>
        {
            SaveManualRunFields();
            MessageBox.Show(this, "Manual Run field values saved - they'll be restored the next time this app opens.",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        // Same accent-color treatment as the other tabs' Save/Refresh buttons - see
        // CreateActionButtonBar - so it reads as a real action, not another gray button
        // indistinguishable from Manual Run/Stop Manual Run at a glance.
        _manualRunSaveButton.BackColor = ActionButtonColor;
        _manualRunSaveButton.ForeColor = Color.White;
        _manualRunSaveButton.FlatStyle = FlatStyle.Flat;
        _manualRunSaveButton.FlatAppearance.BorderSize = 0;
        _manualRunSaveButton.Cursor = Cursors.Hand;
        var manualRunHelp = CreateHelpIcon("Manual Run", ManualRunHelpText);

        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        _manualRunButton.Location = new Point(12, 8);
        _manualRunStopButton.Location = new Point(160, 8);
        _manualRunSaveButton.Location = new Point(310, 8);
        manualRunHelp.Location = new Point(410, 15);
        buttonPanel.Controls.Add(_manualRunButton);
        buttonPanel.Controls.Add(_manualRunStopButton);
        buttonPanel.Controls.Add(_manualRunSaveButton);
        buttonPanel.Controls.Add(manualRunHelp);

        var note = new Label
        {
            Text = "Manual Run executes the sync once, immediately, in its own process. It does not depend on (or " +
                   "start) the Automatic Service, and the two can't run at the same time - see the ? icons for why.",
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(12, 8, 12, 0),
            ForeColor = SystemColors.GrayText
        };

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(note);
        page.Controls.Add(buttonPanel);

        // Tracks 75% of the actual window width live, not a one-time screen-based
        // guess (RunModeWidth's approach) - Resize fires on every window resize
        // (maximizing, dragging an edge, DPI change), and the up-front call sizes it
        // correctly the first time too, before the window is ever resized.
        Resize += (_, _) => UpdateInvoiceNumberListWidth();
        UpdateInvoiceNumberListWidth();

        UpdateRunModeFieldStates();
        LoadManualRunFields(); // restores whatever was last Saved (or last run) - not tied to RefreshAllTabsFromConfig,
                                // since this is local UI state independent of which Service folder/config is loaded,
                                // and re-loading it on every Reload would stomp in-progress edits.
        RefreshAllTabsFromConfig += RefreshDryRunControls;
        return page;
    }

    /// <summary>Persists the current Manual Run field values to the local
    /// admin-settings.json (see MainForm.cs's SaveAdminSettings) - separate from
    /// _appSettings/appsettings.json, since these are per-user UI convenience state
    /// ("what did I last run"), not real Service configuration. Called both from the
    /// dedicated Save button and automatically when a Manual Run actually starts, so
    /// running it once is enough to have it remembered next time even if Save is
    /// never clicked directly.</summary>
    private void SaveManualRunFields()
    {
        SaveAdminSettings(json => json["ManualRun"] = new JsonObject
        {
            ["Mode"] = _runMode.SelectedItem?.ToString() ?? "",
            ["From"] = _runFrom.Value.ToString("O"),
            ["To"] = _runTo.Value.ToString("O"),
            ["StartInvoiceNumber"] = _runStartInvoice.Text,
            ["EndInvoiceNumber"] = _runEndInvoice.Text,
            ["InvoiceNumberList"] = _runInvoiceNumberList.Text,
            ["MaxInvoices"] = (double)_runMaxInvoices.Value
        });
    }

    private void LoadManualRunFields()
    {
        if (LoadAdminSettings()["ManualRun"] is not JsonObject manualRun) return;

        try
        {
            var mode = manualRun["Mode"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(mode) && _runMode.Items.Contains(mode)) _runMode.SelectedItem = mode;

            if (manualRun["From"]?.GetValue<string>() is { } fromText &&
                DateTime.TryParse(fromText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var from))
            {
                _runFrom.Value = from;
            }
            if (manualRun["To"]?.GetValue<string>() is { } toText &&
                DateTime.TryParse(toText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var to))
            {
                _runTo.Value = to;
            }

            _runStartInvoice.Text = manualRun["StartInvoiceNumber"]?.GetValue<string>() ?? _runStartInvoice.Text;
            _runEndInvoice.Text = manualRun["EndInvoiceNumber"]?.GetValue<string>() ?? _runEndInvoice.Text;
            _runInvoiceNumberList.Text = manualRun["InvoiceNumberList"]?.GetValue<string>() ?? _runInvoiceNumberList.Text;

            if (manualRun["MaxInvoices"]?.GetValue<double>() is { } max &&
                (decimal)max >= _runMaxInvoices.Minimum && (decimal)max <= _runMaxInvoices.Maximum)
            {
                _runMaxInvoices.Value = (decimal)max;
            }

            UpdateRunModeFieldStates();
        }
        catch
        {
            // Corrupt/partial saved state - leave whatever didn't parse at its default.
        }
    }

    /// <summary>Keeps the Invoice number list field at 75% of the actual window's
    /// current width, live - not the fixed, screen-size-based guess RunModeWidth
    /// uses for the Mode dropdown, which never changes after the window opens.
    /// Called once up front (BuildRunTab) and again on every Resize.</summary>
    private void UpdateInvoiceNumberListWidth()
    {
        _runInvoiceNumberList.Width = Math.Max(200, (int)(ClientSize.Width * 0.75));
    }

    private void UpdateRunModeFieldStates()
    {
        var mode = _runMode.SelectedIndex;
        _runFrom.Enabled = mode == 0; // Invoice date
        _runTo.Enabled = mode == 0;
        _runStartInvoice.Enabled = mode == 1; // Invoice number range
        _runEndInvoice.Enabled = mode == 1;
        _runInvoiceNumberList.Enabled = mode == 2; // Invoice number list

        // Available for all three remaining modes (Continue/Last changed date,
        // the two that used to be excluded, are gone - see the Mode dropdown's
        // own comment).
        _runOverrideAlreadyImported.Enabled = true;

        // Only meaningful for Invoice date mode (see AdvanceWatermarkHelpText) -
        // hidden/disabled for the other two, and reset to its default every time
        // Invoice date is (re-)selected, since it's never persisted (see the
        // field's own doc comment). Defaults CHECKED (confirmed live 2026-08-25) -
        // an operator running Invoice date mode is, by default, treated as
        // genuinely catching up through that date; uncheck it for a one-off
        // range that shouldn't move the Automatic Service's position.
        var advanceApplicable = mode == 0;
        _runAdvanceWatermark.Visible = advanceApplicable;
        _runAdvanceWatermark.Enabled = advanceApplicable;
        _runAdvanceWatermark.Checked = advanceApplicable;
    }

    /// <summary>Adds the read-only "Previous Run" rows to the given grid - called
    /// once per tab (Manual Run and Automatic Sync), each with its own set of
    /// controls, since a WinForms control can only ever live under one parent.
    /// Uses the same grid as the run parameters above rather than a separately-
    /// docked panel - a TableLayoutPanel's rows always render in row-index order,
    /// so this sidesteps WinForms' well-known "last-docked-control-ends-up-on-top"
    /// ordering gotcha entirely.</summary>
    private void BuildPreviousRunSection(TableLayoutPanel grid, TextBox modeBox, TextBox fromBox, TextBox toBox,
        TextBox maxInvoicesBox, TextBox firstInvoiceBox, TextBox lastInvoiceBox, TextBox resultBox, TextBox invoiceListUsedBox)
    {
        var headingRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label
        {
            Text = "Previous run (excluding \"Finding the Gap\" run)",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 18, 3, 2)
        };
        grid.Controls.Add(heading, 0, headingRow);
        grid.SetColumnSpan(heading, 3);

        AddPreviousRunRowWithCopy(grid, "Previous Run: Mode", modeBox, "RunHistoryEntry.Request.FilterType / UseWatermark");
        AddPreviousRunRowWithCopy(grid, "Previous Run: Inv Start Date", fromBox, "RunHistoryEntry.Result.EffectiveFromUtc",
            "The actual invoice-date window's start, as resolved and used by that run - not the persisted " +
            "watermark, which is generally unrelated to what an explicit Invoice date run actually processed " +
            "unless \"Update Automatic Sync's starting point\" was checked for it. Blank for Invoice number " +
            "range/list modes, which have no date window at all.");
        AddPreviousRunRowWithCopy(grid, "Previous Run: Inv End Date", toBox, "RunHistoryEntry.Result.EffectiveToUtc",
            "The actual invoice-date window's end, as resolved and used by that run.");
        AddPreviousRunRowWithCopy(grid, "Previous Run: Max invoices to process", maxInvoicesBox, "RunHistoryEntry.Request.MaxInvoicesToProcess");
        AddPreviousRunRowWithCopy(grid, "Previous Run: First Invoice Processed", firstInvoiceBox, "Parsed from the run's log (TRANSFER lines)",
            "The lowest-numbered invoice actually transferred to Sage 50 during the previous run - same data as the " +
            "History tab's \"Invoice Transferred\" list, parsed from the log rather than result.json so this works " +
            "for automatic-poll runs too (they never write a result.json).");
        AddPreviousRunRowWithCopy(grid, "Previous Run: Last Invoice Processed", lastInvoiceBox, "Parsed from the run's log (TRANSFER lines)",
            "The highest-numbered invoice actually transferred to Sage 50 during the previous run.");
        AddPreviousRunRowWithCopy(grid, "Previous Run: Result", resultBox, "RunHistoryEntry.Result (Invoices* counts, IsFinal)",
            "A clear pass/fail summary of the previous run, with counts - the same information shown in the pop-up " +
            "when a Manual Run finishes, but kept here too since it applies just as much to the Automatic Service's " +
            "own poll cycles, which run unattended with no pop-up to show.\n\n" +
            "SUCCESS means it completed with no failures. FINISHED WITH ERRORS means it completed but at least one " +
            "invoice failed validation or failed to write - check the Failed Transactions tab or Full Log. " +
            "INTERRUPTED means the process stopped before finishing (crashed, was force-stopped, or hit a fatal " +
            "Sage 50 write error) - the counts shown are as of its last checkpoint, not final.");
        AddPreviousRunRowWithCopy(grid, "Previous Run: Invoice List Used", invoiceListUsedBox, "RunHistoryEntry.Result.ResolvedInvoiceNumberList",
            "The actual comma-separated reference-number list this run used - only populated for Invoice number " +
            "list mode or Find missing invoices in range (gap scan). For a gap scan, this is the REAL computed " +
            "candidate list (everything in the scanned range not already recorded as imported) - the only place " +
            "that list is visible, not just documented in the log. Blank for every other mode. Copy this to paste " +
            "straight into the Invoice number list field above for a Manual Run.");
    }

    /// <summary>Like AddRow(stretchInput: false), but with a "Copy" button
    /// (always enabled, regardless of whether the field is currently empty)
    /// right after the field - confirmed live 2026-08-24 the operator wants to
    /// copy a Previous Run value (most often the resolved Invoice List Used) out
    /// to paste elsewhere, e.g. back into the Invoice number list field above to
    /// re-run it, without needing to manually select the read-only text first.</summary>
    private void AddPreviousRunRowWithCopy(TableLayoutPanel grid, string labelText, TextBox input, string jsonPath, string helpText = "")
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
        input.Anchor = AnchorStyles.Left;
        input.Margin = new Padding(3, 4, 3, 4);

        var copyButton = new Button { Text = "Copy", AutoSize = true, Height = 23, Margin = new Padding(4, 4, 0, 3) };
        copyButton.Click += (_, _) =>
        {
            // Clipboard.SetText throws on an empty string - silently do nothing
            // rather than a jarring error for a field that just happens to be
            // blank for this particular previous run (e.g. Invoice List Used on
            // a date-range run).
            if (!string.IsNullOrEmpty(input.Text)) Clipboard.SetText(input.Text);
        };

        var wrap = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
        wrap.Controls.Add(input);
        wrap.Controls.Add(copyButton);
        if (!string.IsNullOrEmpty(helpText))
        {
            wrap.Controls.Add(CreateHelpIcon(labelText.Replace("\n", " "), helpText));
        }

        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(wrap, 1, row);
        WireSource(input, "(history - most recent completed run)", jsonPath);
    }

    /// <summary>Called every time RefreshHistoryList() runs (MainForm.HistoryTab.cs) -
    /// initial load, after starting/stopping a Manual Run, and on the result-poll
    /// timer - so this section always reflects the actual most recent run, not a
    /// stale snapshot from when the tab was built. Updates both tabs' copies of the
    /// controls together - the underlying data is identical, only the containing
    /// tab differs.</summary>
    private void RefreshPreviousRunSection()
    {
        // Excludes gap-fill sub-runs - every Manual Run automatically triggers its
        // own gap-fill sweep immediately after (see GapFillRunner), which becomes
        // "the most recent run" a moment later and buried what the operator
        // actually ran underneath a narrow, derived range - confirmed live
        // 2026-08-25. entry.Request retains its original FilterType.InvoiceNumberGapScan
        // even though SyncOrchestrator.RunAsync rewrites its own in-memory copy to
        // InvoiceNumberList once it starts (see RunHistoryService's Mode-column
        // label, same check).
        var entry = _historyEntries.FirstOrDefault(e =>
            !e.IsPending && e.Result is not null && e.Request?.FilterType != FilterType.InvoiceNumberGapScan);

        string modeText, fromText, toText, maxInvoicesText, firstInvoiceText, lastInvoiceText, resultText, invoiceListUsedText;
        if (entry?.Result is null)
        {
            modeText = "(no completed run yet)";
            fromText = toText = maxInvoicesText = firstInvoiceText = lastInvoiceText = resultText = invoiceListUsedText = "";
        }
        else
        {
            var request = entry.Request;
            modeText = (request is null
                ? "(automatic poll - continue from where we left off)"
                : request.UseWatermark ? "Continue (from where we left off)" : request.FilterType.ToString())
                + (request?.OverrideAlreadyImportedCheck == true ? " (Override)" : "")
                + (request?.AdvanceWatermarkOnCompletion == true ? " (Watermark Advanced)" : "")
                + (entry.Result.WasDryRun ? " (Dry Run)" : "");
            // The actual resolved invoice-date window (see SyncResult.EffectiveFromUtc's
            // doc comment), not the persisted watermark - the watermark is generally
            // stale/unrelated to what an explicit-range run actually used (unless
            // AdvanceWatermarkOnCompletion was checked for it), which is exactly what
            // left this blank-or-wrong for the runs that prompted this fix.
            fromText = entry.Result.EffectiveFromUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(n/a - no date filter this run)";
            toText = entry.Result.EffectiveToUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(n/a - no date filter this run)";
            maxInvoicesText = request?.MaxInvoicesToProcess?.ToString() ?? "(no limit)";

            // Parsed from the log, not entry.Result.Outcomes - Outcomes is empty for
            // automatic-poll entries (ReconstructFromLogs only recovers the summary
            // counts, not the per-invoice list), so parsing the log is the only way
            // this works identically for every run source.
            var logLines = string.IsNullOrWhiteSpace(_logFolder)
                ? new List<string>()
                : LogExtractorService.ExtractForWindow(_logFolder, entry.Result.StartedAtUtc, entry.Result.FinishedAtUtc);
            var refs = LogExtractorService.ExtractTransferredInvoices(logLines)
                .Select(r => r.PortProReference)
                .Where(r => !string.IsNullOrEmpty(r))
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();

            firstInvoiceText = refs.Count > 0 ? refs[0] : "(none)";
            lastInvoiceText = refs.Count > 0 ? refs[^1] : "(none)";

            // Same three-way classification as ShowRunCompletionMessage's pop-up
            // (MainForm.HistoryTab.cs), condensed to one line - applies here too
            // since this section covers automatic-poll runs, which have no pop-up
            // to show (they run unattended).
            var r = entry.Result;
            var hasFailures = r.InvoicesFailedValidation > 0 || r.InvoicesFailedImport > 0;
            var dryRunPrefix = r.WasDryRun ? "[DRY RUN - simulated, nothing written to Sage 50] " : "";
            resultText = dryRunPrefix + (!r.IsFinal
                ? $"INTERRUPTED before finishing - as of last checkpoint: imported={r.InvoicesImported}, " +
                  $"alreadyImported={r.InvoicesSkippedAlreadyImported}, failedValidation={r.InvoicesFailedValidation}, " +
                  $"failedImport={r.InvoicesFailedImport}. See Failed Transactions / Full Log."
                : hasFailures
                    ? $"FINISHED WITH ERRORS - imported={r.InvoicesImported}, alreadyImported={r.InvoicesSkippedAlreadyImported}, " +
                      $"failedValidation={r.InvoicesFailedValidation}, failedImport={r.InvoicesFailedImport}. See Failed Transactions / Full Log."
                    : $"SUCCESS - imported={r.InvoicesImported}, alreadyImported={r.InvoicesSkippedAlreadyImported}, notFound={r.InvoicesNotFound}.");

            invoiceListUsedText = r.ResolvedInvoiceNumberList ?? "";
        }

        foreach (var (modeBox, fromBox, toBox, maxBox, firstBox, lastBox, resultBox, invoiceListBox) in new[]
        {
            (_prevRunMode, _prevRunFrom, _prevRunTo, _prevRunMaxInvoices, _prevRunFirstInvoiceProcessed, _prevRunLastInvoiceProcessed, _prevRunResult, _prevRunInvoiceListUsed),
            (_syncPrevRunMode, _syncPrevRunFrom, _syncPrevRunTo, _syncPrevRunMaxInvoices, _syncPrevRunFirstInvoiceProcessed, _syncPrevRunLastInvoiceProcessed, _syncPrevRunResult, _syncPrevRunInvoiceListUsed)
        })
        {
            modeBox.Text = modeText;
            fromBox.Text = fromText;
            toBox.Text = toText;
            maxBox.Text = maxInvoicesText;
            firstBox.Text = firstInvoiceText;
            lastBox.Text = lastInvoiceText;
            resultBox.Text = resultText;
            invoiceListBox.Text = invoiceListUsedText;
        }
    }

    /// <summary>Called after Sync tab (re)loads config - the Run/History tabs need the
    /// real folder paths, not a guess, since that's where the Service actually looks.</summary>
    private void RefreshRunTabFolders()
    {
        if (_appSettings is null) return;
        _triggerFolder = _appSettings.GetString("PortProSage.Sync.TriggerFolder");
        _processedTriggerFolder = _appSettings.GetString("PortProSage.Sync.ProcessedTriggerFolder");
        _logFolder = _appSettings.GetString("PortProSage.Sync.LogFolder");
        // Subfolders of TriggerFolder, not TriggerFolder itself - the Worker's
        // trigger-folder scan is non-recursive, so neither Manual Run's nor the
        // Automatic Service's own request/result files here get picked up (and
        // duplicated) by the trigger-watching scan.
        _manualRunFolder = string.IsNullOrWhiteSpace(_triggerFolder) ? "" : Path.Combine(_triggerFolder, "manual");
        _autoPollFolder = string.IsNullOrWhiteSpace(_triggerFolder) ? "" : Path.Combine(_triggerFolder, "auto-poll");
        RefreshHistoryList();
    }

    /// <summary>Called after a Manual Run finishes or is stopped. Mode, From/To, and
    /// Start/End invoice number are deliberately left exactly as they were - the
    /// Previous Run section (read-only, below) already documents what that run used,
    /// and retaining the live inputs too means re-running the same or a similar
    /// range doesn't require re-entering everything. Only Max invoices to process
    /// resets - it's a one-time safety cap, and silently carrying a small cap
    /// forward into an unrelated later run is the one thing worth clearing
    /// automatically. (The date-window footgun this used to guard against - see
    /// BuildRequestFromForm's case 1 comment - is now closed at the source: "Last
    /// changed date" mode always snaps to whole-day boundaries, so retained dates
    /// can't collapse into a near-zero-width window.)
    ///
    /// result is passed (non-null) only from the "run genuinely finished" call site
    /// (MainForm.HistoryTab.cs's ResultPollTimer_Tick), never from Stop Manual Run -
    /// a stopped run isn't "successful", so the Invoice number list check below
    /// never applies there. When it IS successful (no failures at all - IsFinal,
    /// not Skipped, zero failed validation/write, and no whole-run failure Outcome
    /// - see MainForm.HistoryTab.cs's own matching hasFailures check) AND this was
    /// an Invoice number list run (ResolvedInvoiceNumberList is only ever populated
    /// for that mode or a gap-fill sub-run, and a gap-fill sub-run is never what
    /// this poll loop tracks), the list is cleared - confirmed live 2026-08-24 the
    /// operator wants a successfully-processed list gone, not sitting there ready
    /// to be accidentally re-submitted.</summary>
    private void ResetRunFormToDefaults(SyncResult? result = null)
    {
        _runMaxInvoices.Value = 0;
        // One-time override, never persisted (see OverrideAlreadyImportedHelpText) -
        // reset the instant the run it applied to is done, same reasoning as Max
        // invoices above, so it can't silently carry forward into the next run.
        _runOverrideAlreadyImported.Checked = false;

        if (result is { IsFinal: true, Skipped: false } &&
            !string.IsNullOrWhiteSpace(result.ResolvedInvoiceNumberList) &&
            result.InvoicesFailedValidation == 0 && result.InvoicesFailedImport == 0 &&
            !result.Outcomes.Any(o => !o.Success))
        {
            _runInvoiceNumberList.Text = "";
        }
    }

    private SyncRequest BuildRequestFromForm()
    {
        var request = new SyncRequest { RequestedBy = Environment.UserName + " (Admin UI - Manual Run)" };

        switch (_runMode.SelectedIndex)
        {
            case 0:
                // Invoice date - filters by PortPro's billingDate (the invoice's own
                // date), via the billingFrom/billingTo query params (see
                // PortProClient.BuildQueryString's FilterType.CompletedDateRange case -
                // the name is historical/misleading, the actual param is billing-date-
                // based, which IS the invoice's real date). 00:00:01 to 23:59:59 (not
                // midnight-to-midnight) so From is never equal to a boundary the
                // previous day's To could also land on.
                request.FilterType = FilterType.CompletedDateRange;
                request.From = _runFrom.Value.Date.AddSeconds(1);
                request.To = _runTo.Value.Date.AddDays(1).AddSeconds(-1);
                request.AdvanceWatermarkOnCompletion = _runAdvanceWatermark.Checked;
                break;
            case 1:
                request.FilterType = FilterType.InvoiceNumberRange;
                request.StartInvoiceNumber = string.IsNullOrWhiteSpace(_runStartInvoice.Text) ? null : _runStartInvoice.Text.Trim();
                request.EndInvoiceNumber = string.IsNullOrWhiteSpace(_runEndInvoice.Text) ? null : _runEndInvoice.Text.Trim();
                break;
            case 2:
                request.FilterType = FilterType.InvoiceNumberList;
                request.InvoiceNumberList = _runInvoiceNumberList.Text;
                break;
        }

        if (_runMaxInvoices.Value > 0)
        {
            request.MaxInvoicesToProcess = (int)_runMaxInvoices.Value;
        }

        request.OverrideAlreadyImportedCheck = _runOverrideAlreadyImported.Checked;

        return request;
    }

    /// <summary>The actual resolved parameters this run will use - not just "Mode: X",
    /// since that alone doesn't show what dates/numbers/caps were actually resolved
    /// from the form, or which real Sage 50 company file is about to be written to.</summary>
    /// <summary>Deliberately terse (confirmed live 2026-08-24 the previous version -
    /// "Run this now?", the full invoice-number-list dump, and the request file
    /// path - was too much to actually read before clicking Yes/No). Keeps only
    /// what's needed to catch a mistake before it writes to Sage 50 for real: which
    /// company file, which write mode, which selection mode, and the cap - plus
    /// mode-specific detail (date range / invoice range / override warning) only
    /// when it's actually relevant to this particular request, same as before.</summary>
    private string BuildManualRunConfirmationText(SyncRequest request)
    {
        var lines = new List<string>
        {
            // CurrentConfiguredSage50Path (the SAVED value), not
            // _sage50CompanyDataPath.Text (the live field) - confirmed live
            // 2026-08-24 this matters: the Service process only ever reads
            // appsettings.Local.json fresh when it starts, never this Admin
            // instance's in-memory state, so an unsaved edit sitting in the Sage 50
            // tab's path box would show here but the run would silently use
            // whatever's actually saved instead - exactly the kind of mismatch
            // that made a run look like it targeted one company file when it
            // really targeted a completely different one.
            $"Sage 50 path- {CurrentConfiguredSage50Path ?? "(not saved yet - go to the Sage 50 tab and Save first)"}",
            $"Write mode: {(_sage50DryRun.Checked ? "DRY RUN" : "REAL WRITE")}",
            "",
            // Emphasized on its own line, in caps - the actual mode governs which
            // invoices get selected, and it's too easy to click through a
            // confirmation dialog without registering a value buried in a sentence.
            $"MODE: {_runMode.SelectedItem?.ToString()?.ToUpperInvariant()}",
            ""
        };

        if (request.OverrideAlreadyImportedCheck)
        {
            lines.Add("*** OVERRIDE \"ALREADY IMPORTED\" CHECK IS ON - previously-imported invoice(s) will be re-processed. ***");
            lines.Add("");
        }

        if (request.UseWatermark)
        {
            lines.Add("Resolves \"continue from where we left off\" using the persisted watermark - not visible until the Service resolves it.");
        }
        if (request.From is not null || request.To is not null)
        {
            lines.Add($"From: {request.From:yyyy-MM-dd HH:mm}   To: {request.To:yyyy-MM-dd HH:mm}");
        }
        if (request.StartInvoiceNumber is not null || request.EndInvoiceNumber is not null)
        {
            lines.Add($"Start invoice: {request.StartInvoiceNumber ?? "(none)"}   End invoice: {request.EndInvoiceNumber ?? "(none)"}");
        }
        lines.Add($"Max invoices to process: {(request.MaxInvoicesToProcess?.ToString() ?? "no limit")}");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Catches a To-before-From or End-invoice-before-Start-invoice range
    /// before it's ever written to a request file - both are silently "valid" as far
    /// as PortProClient's filtering is concerned (an inverted window just matches
    /// nothing), so without this check the run would simply fetch 0 invoices with no
    /// indication why, the same confusing outcome as the earlier zero-width date bug.</summary>
    private static bool ValidateRequestRanges(SyncRequest request, out string? error)
    {
        error = null;

        if (request.From is not null && request.To is not null && request.From > request.To)
        {
            error = $"From ({request.From:yyyy-MM-dd HH:mm:ss}) is after To ({request.To:yyyy-MM-dd HH:mm:ss}) - " +
                    "To must be on or after From.";
            return false;
        }

        if (!string.IsNullOrEmpty(request.StartInvoiceNumber) && !string.IsNullOrEmpty(request.EndInvoiceNumber) &&
            string.CompareOrdinal(request.EndInvoiceNumber, request.StartInvoiceNumber) < 0)
        {
            error = $"End invoice number ({request.EndInvoiceNumber}) is before Start invoice number " +
                    $"({request.StartInvoiceNumber}) - End must be the same as or come after Start.";
            return false;
        }

        if (request.FilterType == FilterType.InvoiceNumberList && string.IsNullOrWhiteSpace(request.InvoiceNumberList))
        {
            error = "Invoice number list mode needs at least one invoice number - enter one or more, separated by commas.";
            return false;
        }

        return true;
    }

    private void StartManualRun()
    {
        if (string.IsNullOrWhiteSpace(_manualRunFolder))
        {
            MessageBox.Show(this, "Load the Service config first (Sync tab) so the trigger folder is known.", "Not ready",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (state, _) = GetServiceRunState();
        if (state != ServiceRunState.NotRunning)
        {
            MessageBox.Show(this,
                "Something is already running (automatic or manual) - Manual Run and the Automatic Service can't " +
                "run at the same time, since both connect to Sage 50 under the same account.",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!ConfirmProceedIfSage50AppOpen()) return;

        if (!File.Exists(ServiceExePath))
        {
            MessageBox.Show(this, $"Could not find PortProSage.Service.exe in:\n{_serviceFolderBox.Text}", "Not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var request = BuildRequestFromForm();

        if (!ValidateRequestRanges(request, out var rangeError))
        {
            MessageBox.Show(this, rangeError, "Invalid range", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // A separate, dedicated alert - not just a line in the main confirmation
        // dialog below - because re-processing an invoice that's genuinely still in
        // Sage 50 creates a real duplicate transaction, not just a harmless re-check.
        if (request.OverrideAlreadyImportedCheck)
        {
            var overrideConfirm = MessageBox.Show(this,
                "The invoice number(s) from the selected Mode should already be removed from Sage 50 before " +
                "running with \"Override Already Imported\" checked - otherwise this WILL create duplicate " +
                "invoices in Sage 50.\n\nAre you sure you want to proceed?",
                "Confirm override of Already Imported check", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (overrideConfirm != DialogResult.Yes) return;
        }

        var confirm = MessageBox.Show(this, BuildManualRunConfirmationText(request),
            "Confirm manual run", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        SaveManualRunFields(); // so these values are what's shown next time, even if Save was never clicked directly

        Directory.CreateDirectory(_manualRunFolder);
        var requestPath = TriggerService.WriteRequest(_manualRunFolder, request);

        // "Show command window" (Automatic Sync / Manual Run tab, shared/synced
        // setting) - checked shows its own console window like before; unchecked
        // runs it hidden in the background (still fully functional, just no
        // visible window - progress is only watchable via History & Logs then).
        var showWindow = _runShowCommandWindow.Checked;
        _manualRunProcess = Process.Start(new ProcessStartInfo
        {
            FileName = ServiceExePath,
            Arguments = $"--run-once \"{requestPath}\"",
            WorkingDirectory = _serviceFolderBox.Text,
            UseShellExecute = showWindow,
            CreateNoWindow = !showWindow
        });

        _pendingRequestId = request.RequestId;
        _pendingProcessedFolder = _manualRunFolder;
        // An ordinary Manual Run, not a Customer Refresh - explicitly set here
        // (not just left at whatever it was) so a customer refresh that was
        // stopped/interrupted before ResultPollTimer_Tick could clear this itself
        // can never leak into this, unrelated, later run and show the wrong
        // (customer-worded) completion pop-up when this one finishes.
        _pendingRunKind = PendingRunKind.ManualRun;
        _resultPollTimer.Start();

        // Set immediately, not just via the next RefreshServiceStatus() tick -
        // closes any small timing gap where WMI might not yet see the
        // just-launched process's command line right after Process.Start().
        _manualRunButton.Enabled = false;
        _manualRunStopButton.Enabled = true;
        _startServiceButton.Enabled = false;
        _stopServiceButton.Enabled = false;

        RefreshServiceStatus();

        SelectHistoryTab();
        RefreshHistoryList();
        SelectTopHistoryRow(); // the just-started run's own entry - newest RequestedAtUtc, so it's already the top row
    }

    private void StopManualRun()
    {
        if (_manualRunProcess is null) { RefreshServiceStatus(); return; }

        var confirm = MessageBox.Show(this,
            $"Stop this manual run (PID {_manualRunProcess.Id}) now?\n\n" +
            "A graceful shutdown is requested first, so already-imported invoices and the last-processed anchor " +
            "stay correctly recorded up to whatever point it's reached.",
            "Confirm stop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        GracefulStop(_manualRunProcess);
        _resultPollTimer.Stop();
        _pendingRequestId = null;
        _pendingRunKind = PendingRunKind.None;
        RefreshServiceStatus();
        RefreshHistoryList();
        ResetRunFormToDefaults();
        ResetCustomerRefreshFormToDefaults();
    }

    /// <summary>Called by RefreshServiceStatus() (MainForm.ServiceControl.cs) every time
    /// it re-checks what's actually running, so Manual Run's buttons always reflect
    /// reality - including a manual run that finished, or one started outside this app.</summary>
    private void UpdateManualRunButtonStates(ServiceRunState state, Process? process)
    {
        if (state == ServiceRunState.ManualRunning)
        {
            _manualRunButton.Enabled = false;
            // Also covers _customerRefreshScanButton - see UpdateCustomerRefreshRunButtonEnabled,
            // which now routes through UpdateCustomerRefreshScanButtonEnabled too (that one
            // additionally factors in whether the LIVE path is selected, not just service state).
            UpdateCustomerRefreshRunButtonEnabled(false);
            _manualRunStopButton.Enabled = true;
            _manualRunProcess = process;
        }
        else
        {
            _manualRunStopButton.Enabled = false;
            _manualRunProcess = null;
            _manualRunButton.Enabled = state == ServiceRunState.NotRunning;
            UpdateCustomerRefreshRunButtonEnabled(state == ServiceRunState.NotRunning);

            if (_pendingRequestId is not null && state == ServiceRunState.NotRunning)
            {
                // A manual run we were tracking just finished (or was stopped) -
                // one more history refresh in case the result file appeared just
                // after the process exited.
                RefreshHistoryList();
            }
        }
    }
}
