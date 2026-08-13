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

    // Fractions of the primary screen's width, not fixed pixel guesses - falls back
    // to 1920px if Screen.PrimaryScreen is ever unavailable (e.g. headless test run).
    private static readonly int RunModeWidth = (int)((Screen.PrimaryScreen?.Bounds.Width ?? 1920) * 0.25);
    private static readonly int RunInvoiceNumberListWidth = (int)((Screen.PrimaryScreen?.Bounds.Width ?? 1920) * 0.75);

    private ComboBox _runMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = RunModeWidth };
    private DateTimePicker _runFrom = new() { Width = 220 };
    private DateTimePicker _runTo = new() { Width = 220 };
    private TextBox _runStartInvoice = new() { Width = 160 };
    private TextBox _runEndInvoice = new() { Width = 160 };
    private TextBox _runInvoiceNumberList = new() { Width = RunInvoiceNumberListWidth };
    private NumericUpDown _runMaxInvoices = new() { Minimum = 0, Maximum = 100000, Width = 120 };
    private Label _runDryRunStatus = new() { AutoSize = true };
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

    private TabPage BuildRunTab()
    {
        var page = new TabPage("Manual Run");
        var grid = NewFieldGrid();

        _runMode.Items.AddRange(new object[]
        {
            "Invoice date",
            "Continue (from where we left off)",
            "Last changed date",
            "Invoice number range",
            "Invoice number list (comma-separated)"
        });
        // Invoice date is the default, not Continue - it filters by the invoice's own
        // actual date (PortPro's billingDate), so a chosen window can never surprise
        // you with an old invoice that Sage 50 then rejects for being dated before
        // its "Do Not Allow Transactions Dated Before" cutoff. Last changed date
        // filters by when PortPro last TOUCHED an invoice, which is a different
        // thing entirely - confirmed live 2026-08-07 that a Last changed date run
        // pulled in an old invoice merely because it had been recently edited, and
        // Sage 50's date-cutoff rejection killed the whole run over it.
        _runMode.SelectedIndex = 0;
        _runMode.SelectedIndexChanged += (_, _) => UpdateRunModeFieldStates();

        AddRow(grid, "Mode", _runMode, "(request - not a settings file)", "SyncRequest.FilterType / UseWatermark",
            "Picks how invoices get selected for this one run:\n\n" +
            "• Invoice date (default) - invoices whose own date (PortPro's billingDate) falls in the From/To " +
            "window below. This is what you almost always want for a specific date range - it can't surprise you " +
            "with an old invoice that was merely edited recently, unlike Last changed date below.\n" +
            "• Continue - automatically resumes from wherever the last run stopped. Every run, whatever mode it " +
            "used, records two things when it finishes: PortPro's \"last changed\" timestamp of the newest invoice " +
            "it saw (the watermark), and that invoice's reference number. The NEXT Continue run reads that saved " +
            "watermark back, asks PortPro for everything changed since then up to now, processes it, and only then " +
            "moves the watermark forward again - so as long as every run uses Continue, nothing is skipped and " +
            "nothing is reprocessed. No dates/numbers to set. See the \"Previous Run\" section below to confirm " +
            "what the last run actually recorded.\n" +
            "• Last changed date - invoices whose PortPro \"last updated\" time falls in the From/To window below - " +
            "this can include an invoice dated well outside that window if it was simply edited/touched recently, " +
            "which has caused a real run to fail (an old invoice pulled in this way got rejected by Sage 50 for " +
            "being dated before its \"Do Not Allow Transactions Dated Before\" cutoff, killing the run). Prefer " +
            "Invoice date above unless you specifically need \"what changed recently.\"\n" +
            "• Invoice number range - invoices whose reference number falls between Start/End invoice number below, " +
            "with BOTH endpoints included (e.g. Start=90, End=95 processes 90, 91, 92, 93, 94, 95 - 6 invoices, not 5). " +
            "Uses PortPro's paginated list endpoint, scanning the whole account - confirmed live 2026-08-12 this can " +
            "silently miss real invoices the list endpoint excludes for reasons outside our control. If a specific " +
            "invoice you know exists isn't showing up, try Invoice number list below instead.\n" +
            "• Invoice number list - an explicit, comma-separated set of specific invoice numbers (see the field " +
            "below), fetched ONE AT A TIME via PortPro's single-invoice lookup instead of the list endpoint. Slower " +
            "for a large set, but bypasses whatever causes Invoice number range to occasionally miss a real invoice - " +
            "use this to target a small number of specific, known invoice numbers directly.\n\n" +
            "Every mode except Continue is a one-time override - it never reads or changes the saved Continue " +
            "position, so the next Continue run behaves exactly as if the override run never happened.",
            stretchInput: false);
        AddRow(grid, "Invoice Date From", _runFrom, "(request)", "SyncRequest.From",
            "Start of the date window - only used by Invoice date / Last changed date modes.\n\n" +
            "Example: set From to 2026-07-01 and To to 2026-07-31 to process everything from July 2026.",
            stretchInput: false);
        AddRow(grid, "Invoice Date To", _runTo, "(request)", "SyncRequest.To",
            "End of the date window - only used by Invoice date / Last changed date modes.\n\n" +
            "Example: set From to 2026-07-01 and To to 2026-07-31 to process everything from July 2026.",
            stretchInput: false);
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
        AddRow(grid, "Max invoices to process (0 = no limit)", _runMaxInvoices, "(request)", "SyncRequest.MaxInvoicesToProcess",
            "Caps how many eligible (amount > 0) invoices this run actually processes, on top of whatever Mode " +
            "selects - once this many have been handled, the run stops even if more would otherwise qualify. " +
            "0 means no cap.\n\n" +
            "Example: Continue mode with Max invoices = 10 processes only the next 10 unprocessed invoices, " +
            "even if 50 have changed since the last run.");
        AddCheckRow(grid, _runShowCommandWindow, "(request - not a settings file)", "PortProSage:Sync:ShowCommandWindow", ShowCommandWindowHelpText);
        WireShowCommandWindowControl(_runShowCommandWindow);
        AddCheckRow(grid, _runSplitRunByDay, "(request - not a settings file)", "PortProSage:Sync:SplitRunByDay", SplitRunByDayHelpText);
        WireSplitRunByDayControl(_runSplitRunByDay);

        _runDryRunStatus.Text = "Dry run status unknown - load config first.";
        var dryRunRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "Current write mode:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, dryRunRow);
        grid.Controls.Add(_runDryRunStatus, 1, dryRunRow);

        BuildPreviousRunSection(grid, _prevRunMode, _prevRunFrom, _prevRunTo, _prevRunMaxInvoices,
            _prevRunFirstInvoiceProcessed, _prevRunLastInvoiceProcessed, _prevRunResult);

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

        UpdateRunModeFieldStates();
        LoadManualRunFields(); // restores whatever was last Saved (or last run) - not tied to RefreshAllTabsFromConfig,
                                // since this is local UI state independent of which Service folder/config is loaded,
                                // and re-loading it on every Reload would stomp in-progress edits.
        RefreshAllTabsFromConfig += () => _runDryRunStatus.Text = _sage50DryRun.Checked ? "DRY RUN (simulated - nothing written to Sage 50)" : "REAL WRITE (changes Sage 50 for real)";
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

    private void UpdateRunModeFieldStates()
    {
        var mode = _runMode.SelectedIndex;
        _runFrom.Enabled = mode == 0 || mode == 2; // Invoice date, Last changed date
        _runTo.Enabled = mode == 0 || mode == 2;
        _runStartInvoice.Enabled = mode == 3; // Invoice number range
        _runEndInvoice.Enabled = mode == 3;
        _runInvoiceNumberList.Enabled = mode == 4; // Invoice number list
    }

    /// <summary>Adds the read-only "Previous Run" rows to the given grid - called
    /// once per tab (Manual Run and Automatic Sync), each with its own set of
    /// controls, since a WinForms control can only ever live under one parent.
    /// Uses the same grid as the run parameters above rather than a separately-
    /// docked panel - a TableLayoutPanel's rows always render in row-index order,
    /// so this sidesteps WinForms' well-known "last-docked-control-ends-up-on-top"
    /// ordering gotcha entirely.</summary>
    private void BuildPreviousRunSection(TableLayoutPanel grid, TextBox modeBox, TextBox fromBox, TextBox toBox,
        TextBox maxInvoicesBox, TextBox firstInvoiceBox, TextBox lastInvoiceBox, TextBox resultBox)
    {
        var headingRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label
        {
            Text = "Previous Run",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 18, 3, 2)
        };
        grid.Controls.Add(heading, 0, headingRow);
        grid.SetColumnSpan(heading, 3);

        var subHeadingRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var subHeading = new Label
        {
            Text = "Read-only - the parameters and outcome of the most recently completed run (automatic or " +
                   "manual), shown here so it's directly visible that they're retained rather than lost.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 8)
        };
        grid.Controls.Add(subHeading, 0, subHeadingRow);
        grid.SetColumnSpan(subHeading, 3);

        AddRow(grid, "Previous Run: Mode", modeBox, "(history - most recent completed run)", "RunHistoryEntry.Request.FilterType / UseWatermark",
            stretchInput: false);
        AddRow(grid, "Previous Run: Inv Start Date", fromBox, "(history)", "RunHistoryEntry.Result.EffectiveFromUtc",
            "The actual invoice-date window's start, as resolved and used by that run - not the persisted " +
            "watermark, which only ever moves for a Continue run and is otherwise unrelated to what an explicit " +
            "Invoice date/Last changed date run actually processed. Blank for Invoice number range mode, which has " +
            "no date window at all.",
            stretchInput: false);
        AddRow(grid, "Previous Run: Inv End Date", toBox, "(history)", "RunHistoryEntry.Result.EffectiveToUtc",
            "The actual invoice-date window's end, as resolved and used by that run.",
            stretchInput: false);
        AddRow(grid, "Previous Run: Max invoices to process", maxInvoicesBox, "(history)", "RunHistoryEntry.Request.MaxInvoicesToProcess",
            stretchInput: false);
        AddRow(grid, "Previous Run: First Invoice Processed", firstInvoiceBox, "(history)", "Parsed from the run's log (TRANSFER lines)",
            "The lowest-numbered invoice actually transferred to Sage 50 during the previous run - same data as the " +
            "History tab's \"Invoice Transferred\" list, parsed from the log rather than result.json so this works " +
            "for automatic-poll runs too (they never write a result.json).",
            stretchInput: false);
        AddRow(grid, "Previous Run: Last Invoice Processed", lastInvoiceBox, "(history)", "Parsed from the run's log (TRANSFER lines)",
            "The highest-numbered invoice actually transferred to Sage 50 during the previous run.",
            stretchInput: false);
        AddRow(grid, "Previous Run: Result", resultBox, "(history)", "RunHistoryEntry.Result (Invoices* counts, IsFinal)",
            "A clear pass/fail summary of the previous run, with counts - the same information shown in the pop-up " +
            "when a Manual Run finishes, but kept here too since it applies just as much to the Automatic Service's " +
            "own poll cycles, which run unattended with no pop-up to show.\n\n" +
            "SUCCESS means it completed with no failures. FINISHED WITH ERRORS means it completed but at least one " +
            "invoice failed validation or failed to write - check the Failed Transactions tab or Full Log. " +
            "INTERRUPTED means the process stopped before finishing (crashed, was force-stopped, or hit a fatal " +
            "Sage 50 write error) - the counts shown are as of its last checkpoint, not final.");
    }

    /// <summary>Called every time RefreshHistoryList() runs (MainForm.HistoryTab.cs) -
    /// initial load, after starting/stopping a Manual Run, and on the result-poll
    /// timer - so this section always reflects the actual most recent run, not a
    /// stale snapshot from when the tab was built. Updates both tabs' copies of the
    /// controls together - the underlying data is identical, only the containing
    /// tab differs.</summary>
    private void RefreshPreviousRunSection()
    {
        var entry = _historyEntries.FirstOrDefault(e => !e.IsPending && e.Result is not null);

        string modeText, fromText, toText, maxInvoicesText, firstInvoiceText, lastInvoiceText, resultText;
        if (entry?.Result is null)
        {
            modeText = "(no completed run yet)";
            fromText = toText = maxInvoicesText = firstInvoiceText = lastInvoiceText = resultText = "";
        }
        else
        {
            var request = entry.Request;
            modeText = request is null
                ? "(automatic poll - continue from where we left off)"
                : request.UseWatermark ? "Continue (from where we left off)" : request.FilterType.ToString();
            // The actual resolved invoice-date window (see SyncResult.EffectiveFromUtc's
            // doc comment), not the persisted watermark - the watermark only moves for a
            // Continue run and is otherwise stale/unrelated to what an explicit-range run
            // actually used, which is exactly what left this blank-or-wrong for the runs
            // that prompted this fix.
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
            resultText = !r.IsFinal
                ? $"INTERRUPTED before finishing - as of last checkpoint: imported={r.InvoicesImported}, " +
                  $"alreadyImported={r.InvoicesSkippedAlreadyImported}, failedValidation={r.InvoicesFailedValidation}, " +
                  $"failedImport={r.InvoicesFailedImport}. See Failed Transactions / Full Log."
                : hasFailures
                    ? $"FINISHED WITH ERRORS - imported={r.InvoicesImported}, alreadyImported={r.InvoicesSkippedAlreadyImported}, " +
                      $"failedValidation={r.InvoicesFailedValidation}, failedImport={r.InvoicesFailedImport}. See Failed Transactions / Full Log."
                    : $"SUCCESS - imported={r.InvoicesImported}, alreadyImported={r.InvoicesSkippedAlreadyImported}.";
        }

        foreach (var (modeBox, fromBox, toBox, maxBox, firstBox, lastBox, resultBox) in new[]
        {
            (_prevRunMode, _prevRunFrom, _prevRunTo, _prevRunMaxInvoices, _prevRunFirstInvoiceProcessed, _prevRunLastInvoiceProcessed, _prevRunResult),
            (_syncPrevRunMode, _syncPrevRunFrom, _syncPrevRunTo, _syncPrevRunMaxInvoices, _syncPrevRunFirstInvoiceProcessed, _syncPrevRunLastInvoiceProcessed, _syncPrevRunResult)
        })
        {
            modeBox.Text = modeText;
            fromBox.Text = fromText;
            toBox.Text = toText;
            maxBox.Text = maxInvoicesText;
            firstBox.Text = firstInvoiceText;
            lastBox.Text = lastInvoiceText;
            resultBox.Text = resultText;
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
    /// can't collapse into a near-zero-width window.)</summary>
    private void ResetRunFormToDefaults()
    {
        _runMaxInvoices.Value = 0;
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
                // based, which IS the invoice's real date).
                request.FilterType = FilterType.CompletedDateRange;
                request.From = _runFrom.Value.Date.AddSeconds(1);
                request.To = _runTo.Value.Date.AddDays(1).AddSeconds(-1);
                break;
            case 1:
                request.FilterType = FilterType.LastChangedDate;
                request.UseWatermark = true;
                break;
            case 2:
                request.FilterType = FilterType.LastChangedDate;
                // Whole calendar days, not the picker's raw Value - the DateTimePicker
                // only ever DISPLAYS a date (no time-of-day control), so "From: June 22,
                // To: June 22" looks like a real window even when the two Values are
                // actually only milliseconds apart (their untouched construction-time
                // default) - confirmed live 2026-08-07 this produced a real run with a
                // 15-millisecond window and, unsurprisingly, 0 invoices fetched.
                // 00:00:01 to 23:59:59 (not midnight-to-midnight) so From is never equal
                // to a boundary the previous day's To could also land on.
                request.From = _runFrom.Value.Date.AddSeconds(1);
                request.To = _runTo.Value.Date.AddDays(1).AddSeconds(-1);
                break;
            case 3:
                request.FilterType = FilterType.InvoiceNumberRange;
                request.StartInvoiceNumber = string.IsNullOrWhiteSpace(_runStartInvoice.Text) ? null : _runStartInvoice.Text.Trim();
                request.EndInvoiceNumber = string.IsNullOrWhiteSpace(_runEndInvoice.Text) ? null : _runEndInvoice.Text.Trim();
                break;
            case 4:
                request.FilterType = FilterType.InvoiceNumberList;
                request.InvoiceNumberList = _runInvoiceNumberList.Text;
                break;
        }

        if (_runMaxInvoices.Value > 0)
        {
            request.MaxInvoicesToProcess = (int)_runMaxInvoices.Value;
        }

        return request;
    }

    /// <summary>The actual resolved parameters this run will use - not just "Mode: X",
    /// since that alone doesn't show what dates/numbers/caps were actually resolved
    /// from the form, or which real Sage 50 company file is about to be written to.</summary>
    private string BuildManualRunConfirmationText(SyncRequest request, string requestPathPreview)
    {
        var lines = new List<string>
        {
            "Run this now?",
            "",
            $"Write mode: {(_sage50DryRun.Checked ? "DRY RUN (simulated - nothing written to Sage 50)" : "REAL WRITE (changes Sage 50 for real)")}",
            $"Sage 50 company file: {_sage50CompanyDataPath.Text}",
            "",
            // Emphasized on its own line, in caps - the actual mode governs which
            // invoices get selected, and it's too easy to click through a
            // confirmation dialog without registering a value buried in a sentence.
            $"MODE: {_runMode.SelectedItem?.ToString()?.ToUpperInvariant()}",
            ""
        };

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
        if (!string.IsNullOrWhiteSpace(request.InvoiceNumberList))
        {
            lines.Add($"Invoice numbers: {request.InvoiceNumberList}");
        }
        lines.Add($"Max invoices to process: {(request.MaxInvoicesToProcess?.ToString() ?? "no limit")}");
        lines.Add("");
        lines.Add($"Request will be written to:\n{requestPathPreview}");

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

        var requestPathPreview = Path.Combine(_manualRunFolder, $"{request.RequestId}.request.json");

        var confirm = MessageBox.Show(this, BuildManualRunConfirmationText(request, requestPathPreview),
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
        RefreshServiceStatus();
        RefreshHistoryList();
        ResetRunFormToDefaults();
    }

    /// <summary>Called by RefreshServiceStatus() (MainForm.ServiceControl.cs) every time
    /// it re-checks what's actually running, so Manual Run's buttons always reflect
    /// reality - including a manual run that finished, or one started outside this app.</summary>
    private void UpdateManualRunButtonStates(ServiceRunState state, Process? process)
    {
        if (state == ServiceRunState.ManualRunning)
        {
            _manualRunButton.Enabled = false;
            _manualRunStopButton.Enabled = true;
            _manualRunProcess = process;
        }
        else
        {
            _manualRunStopButton.Enabled = false;
            _manualRunProcess = null;
            _manualRunButton.Enabled = state == ServiceRunState.NotRunning;

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
