using PortProSage.Admin.Services;

namespace PortProSage.Admin;

public partial class MainForm
{
    private DataGridView _historyGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };

    private TextBox _historySummaryText = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private DataGridView _historyOutcomesGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private TextBox _historyWarningsText = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    private TextBox _historyFailedText = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    private TextBox _historyLogSearchBox = new() { Dock = DockStyle.Top };
    private TextBox _historyLogText = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
    private DataGridView _historyTransferredGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };

    private List<RunHistoryEntry> _historyEntries = new();
    private List<string> _selectedRunLogLines = new();

    private TabPage BuildResultsTab()
    {
        var page = new TabPage("History && Logs");

        SetupHistoryGrid();
        SetupOutcomesGrid();

        var refreshButton = new Button { Text = "Refresh", Dock = DockStyle.Top, Height = 30 };
        refreshButton.Click += (_, _) => RefreshHistoryList();

        // Fixed height, not a resizable SplitContainer - deterministically
        // shows exactly 15 rows, computed from the fixed row/header heights
        // pinned in SetupHistoryGrid. A Panel's Height is a plain absolute
        // value with none of SplitContainer.SplitterDistance's baggage (it
        // validates against the container's OWN current size, which isn't
        // reliably known until the tab has actually been shown at least once -
        // confirmed live, repeatedly, trying to make that work before settling
        // on this instead).
        var topPanel = new Panel { Dock = DockStyle.Top, Height = HistoryGridHeaderHeight + HistoryGridRowHeight * 15 + 2 };
        topPanel.Controls.Add(_historyGrid);

        var detailTabs = new TabControl { Dock = DockStyle.Fill };

        var summaryPage = new TabPage("Summary");
        summaryPage.Controls.Add(_historySummaryText);

        var outcomesPage = new TabPage("Per-invoice outcomes");
        outcomesPage.Controls.Add(_historyOutcomesGrid);

        var warningsPage = new TabPage("Warnings / Validation");
        warningsPage.Controls.Add(_historyWarningsText);

        var failedPage = new TabPage("Failed Transactions");
        failedPage.Controls.Add(_historyFailedText);

        var logPage = new TabPage("Full log");
        _historyLogSearchBox.PlaceholderText = "Search this run's log (filters as you type)...";
        _historyLogSearchBox.TextChanged += (_, _) => ApplyLogSearchFilter();
        var logPanel = new Panel { Dock = DockStyle.Fill };
        logPanel.Controls.Add(_historyLogText);
        logPanel.Controls.Add(_historyLogSearchBox);
        logPage.Controls.Add(logPanel);

        SetupTransferredGrid();
        var transferredPage = new TabPage("Invoice Transferred");
        transferredPage.Controls.Add(_historyTransferredGrid);

        detailTabs.TabPages.Add(summaryPage);
        detailTabs.TabPages.Add(outcomesPage);
        detailTabs.TabPages.Add(transferredPage);
        detailTabs.TabPages.Add(warningsPage);
        detailTabs.TabPages.Add(failedPage);
        detailTabs.TabPages.Add(logPage);

        // detailTabs (Dock=Fill) picks up whatever topPanel's fixed 15-row
        // height doesn't use - added before refreshButton/topPanel below so it
        // doesn't matter for Dock=Fill (always "whatever's left" regardless of
        // add order), only their own relative order among each other matters
        // (refreshButton added last so it lands at the true top edge, above
        // topPanel - see the "last-docked-control-ends-up-on-top" note
        // elsewhere in this app).
        page.Controls.Add(detailTabs);
        page.Controls.Add(topPanel);
        page.Controls.Add(refreshButton);

        _historyGrid.SelectionChanged += (_, _) => ShowSelectedHistoryEntry();

        return page;
    }

    // Pinned to known, fixed values (not left to font/DPI-dependent defaults)
    // so topPanel's Height (BuildResultsTab) can be computed directly at
    // construction time, with nothing left to measure or retry later.
    private const int HistoryGridRowHeight = 22;
    private const int HistoryGridHeaderHeight = 26;

    private void SetupHistoryGrid()
    {
        // DataGridView doesn't expose DoubleBuffered publicly - this is the standard
        // reflection workaround. Cuts down the visible redraw flicker every time
        // RefreshHistoryList() clears and rebuilds every row (every 2 seconds while
        // a Manual Run is actively being polled) - the real fix for that flicker was
        // making the poll actually stop once there's nothing left to wait for (see
        // ResultPollTimer_Tick), but this still helps for the genuinely-active window.
        typeof(DataGridView).InvokeMember("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.SetProperty,
            null, _historyGrid, new object[] { true });

        _historyGrid.RowTemplate.Height = HistoryGridRowHeight;
        _historyGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _historyGrid.ColumnHeadersHeight = HistoryGridHeaderHeight;

        // Explicit widths, not a uniform Fill across every column - only the
        // genuinely long string column (Request ID) should be wide; date columns
        // are sized to fit an actual date/time string, and count columns are
        // sized to fit a small number, not stretched for no reason.
        _historyGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

        _historyGrid.Columns.Add("Seq", "#");
        _historyGrid.Columns.Add("RequestId", "Request ID");
        _historyGrid.Columns.Add("Source", "Source");
        _historyGrid.Columns.Add("Mode", "Mode");
        _historyGrid.Columns.Add("Started", "Process Start");
        _historyGrid.Columns.Add("Finished", "Process End");
        _historyGrid.Columns.Add("InvStart", "Inv Start Date");
        _historyGrid.Columns.Add("InvEnd", "Inv End Date");
        _historyGrid.Columns.Add("Fetched", "Fetched");
        _historyGrid.Columns.Add("Imported", "Imported");
        _historyGrid.Columns.Add("Skipped", "Already imported");
        _historyGrid.Columns.Add("SkippedCutoff", "Before cutoff");
        _historyGrid.Columns.Add("FailedVal", "Failed validation");
        _historyGrid.Columns.Add("FailedImp", "Failed write");
        _historyGrid.Columns.Add("Status", "Status");

        _historyGrid.Columns["Seq"].Width = 45;
        _historyGrid.Columns["Source"].Width = 110;
        _historyGrid.Columns["Mode"].Width = 130;
        _historyGrid.Columns["Started"].Width = 130;
        _historyGrid.Columns["Finished"].Width = 130;
        _historyGrid.Columns["InvStart"].Width = 100;
        _historyGrid.Columns["InvEnd"].Width = 100;
        _historyGrid.Columns["Fetched"].Width = 60;
        _historyGrid.Columns["Imported"].Width = 65;
        _historyGrid.Columns["Skipped"].Width = 100;
        _historyGrid.Columns["SkippedCutoff"].Width = 90;
        _historyGrid.Columns["FailedVal"].Width = 100;
        _historyGrid.Columns["FailedImp"].Width = 90;
        _historyGrid.Columns["Status"].Width = 100;

        // Sized to fit its actual displayed content (all rows), not stretched to
        // absorb whatever's left - Request IDs are a consistent-length GUID, so
        // this settles at a fitting width rather than an arbitrarily large one.
        _historyGrid.Columns["RequestId"].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;

        // Now that Request ID no longer soaks up the leftover space, something
        // else has to, or the grid just leaves a growing gray gap on the right as
        // the window widens (confirmed live - that's exactly what happened).
        // Status is the last column, so it stretching doesn't disrupt the visual
        // flow of the numeric/date columns in the middle of the table.
        _historyGrid.Columns["Status"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
    }

    private void SetupOutcomesGrid()
    {
        _historyOutcomesGrid.Columns.Add("Reference", "Invoice #");
        _historyOutcomesGrid.Columns.Add("Success", "Success");
        _historyOutcomesGrid.Columns.Add("SageNumber", "Sage 50 #");
        _historyOutcomesGrid.Columns.Add("Messages", "Messages");

        // Proportional widths (FillWeight, not pixels) - AutoSizeColumnsMode.Fill is
        // already set on the grid itself (see field declaration above), so these sum
        // to 100 and read directly as percentages of the available width.
        _historyOutcomesGrid.Columns["Reference"].FillWeight = 20;
        _historyOutcomesGrid.Columns["Success"].FillWeight = 20;
        _historyOutcomesGrid.Columns["SageNumber"].FillWeight = 20;
        _historyOutcomesGrid.Columns["Messages"].FillWeight = 40;
    }

    private void SetupTransferredGrid()
    {
        // Built from the run's full log (see LogExtractorService.ExtractTransferredInvoices),
        // not from result.json's Outcomes - the automatic poll never writes a result.json,
        // so the log is the only record that exists for those runs.
        _historyTransferredGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _historyTransferredGrid.Columns.Add("PortProRef", "PortPro Invoice #");
        _historyTransferredGrid.Columns.Add("PortProDate", "PortPro Date");
        _historyTransferredGrid.Columns.Add("Sage50Number", "Sage 50 Invoice #");
        _historyTransferredGrid.Columns.Add("Sage50Date", "Sage 50 Date");
        _historyTransferredGrid.Columns.Add("TotalAmount", "Total Amount");
        _historyTransferredGrid.Columns.Add("TaxCharged", "Tax Charged");

        // Proportional widths (FillWeight, not pixels) - sums to 100, so these read
        // directly as percentages of the available width.
        _historyTransferredGrid.Columns["PortProRef"].FillWeight = 25;
        _historyTransferredGrid.Columns["PortProDate"].FillWeight = 15;
        _historyTransferredGrid.Columns["Sage50Number"].FillWeight = 25;
        _historyTransferredGrid.Columns["Sage50Date"].FillWeight = 15;
        _historyTransferredGrid.Columns["TotalAmount"].FillWeight = 10;
        _historyTransferredGrid.Columns["TaxCharged"].FillWeight = 10;

        _historyTransferredGrid.Columns["TotalAmount"].DefaultCellStyle.Format = "N2";
        _historyTransferredGrid.Columns["TaxCharged"].DefaultCellStyle.Format = "N2";
    }

    private void SelectHistoryTab()
    {
        foreach (TabPage tab in _tabs.TabPages)
        {
            if (tab.Text.StartsWith("History")) { _tabs.SelectedTab = tab; break; }
        }
    }

    private void RefreshHistoryList()
    {
        if (string.IsNullOrWhiteSpace(_triggerFolder) || string.IsNullOrWhiteSpace(_processedTriggerFolder)) return;

        var selectedId = (_historyGrid.SelectedRows.Count > 0) ? _historyGrid.SelectedRows[0].Cells["RequestId"].Value?.ToString() : null;

        _historyEntries = RunHistoryService.ListRuns(_triggerFolder, _processedTriggerFolder, _logFolder, _manualRunFolder, _autoPollFolder);
        _historyGrid.Rows.Clear();
        RefreshPreviousRunSection();

        // A short, stable reference number for each run, cheaper to say/type than
        // the full Request ID GUID - assigned by chronological (ascending) order so
        // a given run keeps the same number forever as new runs are added, rather
        // than by the grid's own descending display order, which would renumber
        // every existing row each time a new run appears.
        var seqByRequestId = _historyEntries
            .OrderBy(e => e.SortKey)
            .Select((e, idx) => (e.RequestId, Seq: idx + 1))
            .ToDictionary(x => x.RequestId, x => x.Seq);

        // Which manual request (if any) a currently-live --run-once process actually
        // belongs to - the only way to tell a pending manual entry that's genuinely
        // still running apart from one whose process is already gone.
        var (liveState, liveProcess) = GetServiceRunState();
        var liveManualCommandLine = liveState == ServiceRunState.ManualRunning && liveProcess is not null
            ? GetCommandLine(liveProcess.Id) ?? ""
            : "";

        for (var i = 0; i < _historyEntries.Count; i++)
        {
            var entry = _historyEntries[i];

            // Three flavors of "pending with no result", all needing a live-process
            // check: a Manual Run (its own dedicated --run-once process, matched by
            // RequestId in the command line), an automatic-poll cycle whose request
            // file has no matching result file (crashed, or hit Sage50Client.
            // TerminateOnFatalWriteError's Environment.Exit before RunAsync could
            // return and Worker.cs could write one), and the legacy log-reconstructed
            // fallback for auto-poll cycles that predate that file-based tracking.
            // The Automatic Service has no per-cycle process to match against like
            // Manual Run does, so both auto-poll flavors are only "live" if this is
            // the single most recent thing in all of history AND the Automatic
            // Service is actually running right now.
            var isAutoPollFlavor = entry.IsAutomaticPoll || entry.ReconstructedFromLog;
            if (entry.IsPending && (entry.IsManual || isAutoPollFlavor))
            {
                entry.IsLiveProcess = entry.IsManual
                    ? liveManualCommandLine.Contains(entry.RequestId, StringComparison.OrdinalIgnoreCase)
                    : i == 0 && liveState == ServiceRunState.AutomaticRunning;

                // Tie up the loose end: the process that was supposed to handle this
                // request is gone without ever writing a result (not even a
                // checkpoint - see the IsFinal-carrying branch above for when one
                // exists). Pull the last thing it actually logged so there's still
                // an end time and something to look at here, instead of "Running"
                // forever with a blank Finished column.
                if (!entry.IsLiveProcess && entry.Result is null && !string.IsNullOrWhiteSpace(_logFolder))
                {
                    var window = GetLogWindow(entry, i);
                    if (window is not null)
                    {
                        var lines = LogExtractorService.ExtractForWindow(_logFolder, window.Value.Start, window.Value.End);
                        entry.LastLogActivityUtc = LogExtractorService.GetLastTimestamp(lines);
                    }
                }
            }

            // A pre-flight-skipped automatic-poll cycle (Worker.cs found another
            // Service process already running) never actually ran anything - show
            // that plainly in Mode/Status instead of the usual FilterType-derived
            // text, which would otherwise misleadingly read as a real Continue/
            // LastChangedDate run that just happened to fetch 0 invoices.
            var mode = entry.Result?.Skipped == true
                ? "Skipped - Process Running"
                : entry.Request is not null
                    ? (entry.Request.UseWatermark ? "Continue" : entry.Request.FilterType.ToString())
                    : "(auto-poll)";
            var source = entry.IsAutomaticPoll || entry.ReconstructedFromLog ? "Automatic Service"
                : entry.IsManual ? "Manual Run"
                : "Trigger file";

            string status;
            string finishedText;
            if (entry.Result?.Skipped == true)
            {
                status = "Skipped";
                finishedText = entry.Result.FinishedAtUtc.ToLocalTime().ToString("g");
            }
            else if (!entry.IsPending)
            {
                status = "Completed";
                finishedText = entry.Result?.FinishedAtUtc.ToLocalTime().ToString("g") ?? "";
            }
            else if ((entry.IsManual || isAutoPollFlavor) && entry.IsLiveProcess)
            {
                status = "Running";
                finishedText = "";
            }
            else if ((entry.IsManual || isAutoPollFlavor) && entry.Result is not null)
            {
                // A checkpoint exists (SyncOrchestrator.RunAsync's onProgress callback
                // wrote it) but IsFinal never got set - the process died mid-run. The
                // counts below are real, as-of-last-checkpoint numbers, not blanks.
                status = "Interrupted (partial)";
                finishedText = entry.Result.FinishedAtUtc.ToLocalTime().ToString("g");
            }
            else if (entry.IsManual || isAutoPollFlavor)
            {
                status = "Interrupted (no result)";
                finishedText = entry.LastLogActivityUtc?.ToLocalTime().ToString("g") ?? "";
            }
            else
            {
                status = "Pending (queued)";
                finishedText = "";
            }

            var rowIndex = _historyGrid.Rows.Add(
                seqByRequestId.GetValueOrDefault(entry.RequestId, 0),
                entry.RequestId,
                source,
                mode,
                entry.Result?.StartedAtUtc.ToLocalTime().ToString("g") ?? entry.Request?.RequestedAtUtc.ToLocalTime().ToString("g") ?? "",
                finishedText,
                entry.Result?.EffectiveFromUtc?.ToLocalTime().ToString("g") ?? "",
                entry.Result?.EffectiveToUtc?.ToLocalTime().ToString("g") ?? "",
                entry.Result?.InvoicesFetched.ToString() ?? "",
                entry.Result?.InvoicesImported.ToString() ?? "",
                entry.Result?.InvoicesSkippedAlreadyImported.ToString() ?? "",
                entry.Result?.InvoicesSkippedBeforeCutoff.ToString() ?? "",
                entry.Result?.InvoicesFailedValidation.ToString() ?? "",
                entry.Result?.InvoicesFailedImport.ToString() ?? "",
                status);
            _historyGrid.Rows[rowIndex].Tag = entry;
        }

        if (selectedId is not null)
        {
            foreach (DataGridViewRow row in _historyGrid.Rows)
            {
                if (row.Cells["RequestId"].Value?.ToString() == selectedId) { row.Selected = true; break; }
            }
        }
    }

    /// <summary>The log time-window for a history entry - Result's own
    /// StartedAtUtc/FinishedAtUtc when we have them; otherwise the Request's
    /// RequestedAtUtc as a start, and either "now" (a genuinely still-live process)
    /// or the next chronological entry's start (the Worker's loop is single-
    /// threaded, so nothing else could have logged in between) as the end. Null if
    /// there isn't even a start to work with.</summary>
    private (DateTimeOffset Start, DateTimeOffset End)? GetLogWindow(RunHistoryEntry entry, int index)
    {
        var start = entry.Result?.StartedAtUtc ?? entry.Request?.RequestedAtUtc;
        if (start is null) return null;
        if (entry.Result?.FinishedAtUtc is { } finished) return (start.Value, finished);
        if (entry.IsLiveProcess) return (start.Value, DateTimeOffset.UtcNow);

        var nextEntry = index > 0 ? _historyEntries[index - 1] : null;
        var end = nextEntry?.Result?.StartedAtUtc ?? nextEntry?.Request?.RequestedAtUtc ?? DateTimeOffset.UtcNow;
        return (start.Value, end);
    }

    private void ResultPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_pendingRequestId is null || _pendingProcessedFolder is null) { _resultPollTimer.Stop(); return; }

        var result = TriggerService.TryReadResult(_pendingProcessedFolder, _pendingRequestId);
        if (result is not null)
        {
            _pendingRequestId = null;
            _resultPollTimer.Stop();
            RefreshHistoryList();
            ResetRunFormToDefaults();
            return;
        }

        // Confirmed live 2026-08-07 this was the actual cause of constant History &
        // Logs flicker (and a selected row's Summary flashing then disappearing): a
        // run whose process died without ever writing a result.json (crashed, or hit
        // TerminateOnFatalWriteError) left result permanently null forever, so this
        // timer never stopped - it just kept calling RefreshHistoryList() (full grid
        // rebuild, briefly deselecting the current row) every 2 seconds indefinitely,
        // long after there was anything left to actually wait for. Stop as soon as
        // the process we're tracking is confirmed gone, not just when we find a result.
        var (state, process) = GetServiceRunState();
        var stillTrackingThisRequest = state == ServiceRunState.ManualRunning && process is not null &&
            (GetCommandLine(process.Id) ?? "").Contains(_pendingRequestId, StringComparison.OrdinalIgnoreCase);

        if (!stillTrackingThisRequest)
        {
            _pendingRequestId = null;
            _resultPollTimer.Stop();
            RefreshHistoryList();
            return;
        }

        RefreshHistoryList();
    }

    private void ShowSelectedHistoryEntry()
    {
        _historyOutcomesGrid.Rows.Clear();
        _historyTransferredGrid.Rows.Clear();
        _historyLogText.Text = "";
        _historyWarningsText.Text = "";
        _historyFailedText.Text = "";
        _selectedRunLogLines = new List<string>();

        if (_historyGrid.SelectedRows.Count == 0 || _historyGrid.SelectedRows[0].Tag is not RunHistoryEntry entry)
        {
            _historySummaryText.Text = "";
            return;
        }

        _historySummaryText.Text = BuildSummaryText(entry);

        if (entry.Result is not null)
        {
            foreach (var outcome in entry.Result.Outcomes)
            {
                _historyOutcomesGrid.Rows.Add(
                    outcome.ReferenceNumber,
                    outcome.Success ? "Yes" : "No",
                    outcome.Sage50InvoiceNumber ?? "",
                    string.Join(" | ", outcome.Messages));
            }
        }

        // Extracted regardless of whether entry.Result exists - a run that started
        // but never got a result.json (crashed, or was killed before it could write
        // one) still has real log lines sitting in the Service's log file explaining
        // what happened, and that's exactly the case where seeing them matters most.
        var selectedIndex = _historyGrid.SelectedRows[0].Index;
        var window = GetLogWindow(entry, selectedIndex);
        if (window is not null && !string.IsNullOrWhiteSpace(_logFolder))
        {
            _selectedRunLogLines = LogExtractorService.ExtractForWindow(_logFolder, window.Value.Start, window.Value.End);
        }

        // Warnings/Validation and Failed Transactions are both filtered views of
        // the exact same full log, not separately-fetched data - "[WRN]" catches
        // Serilog warning-level lines (auto-answered Sage 50 confirmations,
        // duplicate-invoice auto-recovery, etc.) plus "VALIDATION:" catches the
        // per-invoice validation-failure detail embedded in the outcome-summary
        // lines; "[ERR]"/"[FTL]" catches real write failures/crashes, plus
        // "success=False" catches every per-invoice line for an invoice that
        // didn't succeed, validation or write alike.
        _historyWarningsText.Text = string.Join(Environment.NewLine, _selectedRunLogLines.Where(l =>
            l.Contains("[WRN]", StringComparison.Ordinal) || l.Contains("VALIDATION:", StringComparison.OrdinalIgnoreCase)));
        _historyFailedText.Text = string.Join(Environment.NewLine, _selectedRunLogLines.Where(l =>
            l.Contains("[ERR]", StringComparison.Ordinal) || l.Contains("[FTL]", StringComparison.Ordinal) ||
            l.Contains("success=False", StringComparison.OrdinalIgnoreCase)));

        foreach (var row in LogExtractorService.ExtractTransferredInvoices(_selectedRunLogLines))
        {
            _historyTransferredGrid.Rows.Add(
                row.PortProReference, row.PortProDate, row.Sage50InvoiceNumber, row.Sage50Date,
                row.TotalAmount, row.TaxCharged);
        }

        ApplyLogSearchFilter();
    }

    private static string BuildSummaryText(RunHistoryEntry entry)
    {
        var lines = new List<string>
        {
            $"Request ID: {entry.RequestId}",
            (entry.IsAutomaticPoll || entry.ReconstructedFromLog) && entry.IsPending && !entry.IsLiveProcess
                ? "Source: Automatic poll - started this cycle but the process is no longer running (see below)"
                : entry.IsAutomaticPoll ? "Source: Automatic poll" :
                entry.ReconstructedFromLog ? "Source: Automatic poll (reconstructed from the log - predates this install writing a request/result file per cycle)" :
                entry.IsManual && entry.IsPending && !entry.IsLiveProcess ? "Source: Manual trigger - the process handling it is no longer running (see below)" :
                entry.IsPending ? "Source: Manual trigger - still pending, not processed yet" : "Source: Manual trigger",
        };

        if (entry.Request is not null)
        {
            lines.Add($"Requested by: {entry.Request.RequestedBy}");
            lines.Add($"Filter type: {entry.Request.FilterType}, UseWatermark: {entry.Request.UseWatermark}");
            if (entry.Request.From is not null || entry.Request.To is not null)
                lines.Add($"From: {entry.Request.From:g}   To: {entry.Request.To:g}");
            if (entry.Request.StartInvoiceNumber is not null || entry.Request.EndInvoiceNumber is not null)
                lines.Add($"Start invoice: {entry.Request.StartInvoiceNumber}   End invoice: {entry.Request.EndInvoiceNumber}");
            if (entry.Request.MaxInvoicesToProcess is not null)
                lines.Add($"Max invoices to process: {entry.Request.MaxInvoicesToProcess}");
        }

        if (entry.Result is null && entry.Request is not null)
        {
            lines.Add("");
            lines.Add($"Process Start: {entry.Request.RequestedAtUtc.ToLocalTime():G}");

            if (entry.IsLiveProcess)
            {
                lines.Add("Still running - no result yet.");
            }
            else
            {
                lines.Add($"Last log activity: {entry.LastLogActivityUtc?.ToLocalTime().ToString("G") ?? "(none found)"}");
                lines.Add("No result was ever recorded for this run - the process that was handling it is no " +
                           "longer running (crashed, was force-stopped, or hit a fatal condition that terminates " +
                           "the process immediately - see Sage50Client.TerminateOnFatalWriteError, which does " +
                           "exactly this on an unrecoverable Sage 50 write error). Check the Full log / Failed " +
                           "Transactions tabs below for exactly what it logged before stopping.");
            }
        }

        if (entry.Result is not null)
        {
            lines.Add("");
            lines.Add($"Process Start: {entry.Result.StartedAtUtc.ToLocalTime():G}");
            lines.Add($"Process End:   {entry.Result.FinishedAtUtc.ToLocalTime():G}");
            lines.Add($"Inv Start Date: {entry.Result.EffectiveFromUtc?.ToLocalTime().ToString("G") ?? "(n/a - no date filter this run)"}");
            lines.Add($"Inv End Date:   {entry.Result.EffectiveToUtc?.ToLocalTime().ToString("G") ?? "(n/a - no date filter this run)"}");
            if (entry.Result.BatchCount > 1)
            {
                lines.Add($"Batches: {entry.Result.BatchCount} (split by day)");
            }

            if (entry.Result.Skipped)
            {
                lines.Add("");
                lines.Add("SKIPPED - nothing was actually attempted this cycle.");
                lines.Add(entry.Result.SkipReason ?? "(no reason recorded)");
                return string.Join(Environment.NewLine, lines);
            }

            lines.Add($"Duration: {(entry.Result.FinishedAtUtc - entry.Result.StartedAtUtc).TotalSeconds:0.0} sec");

            if (entry.IsPending && !entry.IsLiveProcess)
            {
                lines.Add("");
                lines.Add("INTERRUPTED - the process stopped before finishing. The counts below are real, " +
                           "as of its last checkpoint (saved after every invoice), not blanks.");
            }

            lines.Add("");
            lines.Add($"Fetched: {entry.Result.InvoicesFetched}");
            lines.Add($"Imported (real writes this run): {entry.Result.InvoicesImported}");
            lines.Add($"Already imported (skipped): {entry.Result.InvoicesSkippedAlreadyImported}");
            lines.Add($"Zero/negative amount (skipped): {entry.Result.InvoicesSkippedZeroOrNegativeAmount}");
            lines.Add($"Before cutoff invoice date (skipped): {entry.Result.InvoicesSkippedBeforeCutoff}");
            lines.Add($"Failed validation: {entry.Result.InvoicesFailedValidation}");
            lines.Add($"Failed write: {entry.Result.InvoicesFailedImport}");
            lines.Add("");

            // Pre/post snapshot of persisted state - Start == End confirms an
            // explicit-range run genuinely left the anchor untouched (the
            // guarantee UseWatermark=False is supposed to make); for a
            // watermark-driven run, the gap between them is exactly how far
            // this run advanced.
            lines.Add($"Start Watermark (date): {entry.Result.WatermarkBeforeRun?.ToLocalTime().ToString("G") ?? "(none yet)"}");
            lines.Add($"End Watermark (date):   {entry.Result.WatermarkAfterRun?.ToLocalTime().ToString("G") ?? "(none yet)"}");
            lines.Add($"Start Watermark (invoice #): {entry.Result.LastProcessedInvoiceNumberBeforeRun ?? "(none yet)"}");
            lines.Add($"End Watermark (invoice #):   {entry.Result.LastProcessedInvoiceNumberAfterRun ?? "(none yet)"}");

            var referenceNumbers = entry.Result.Outcomes
                .Select(o => o.ReferenceNumber)
                .Where(r => !string.IsNullOrEmpty(r))
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();
            if (referenceNumbers.Count > 0)
            {
                lines.Add($"Processed invoices: {referenceNumbers[0]} to {referenceNumbers[^1]} ({referenceNumbers.Count} total)");
            }

            if (entry.Result.LastProcessedInvoiceNumberAfterRun is not null)
                lines.Add($"Anchor advanced to: {entry.Result.LastProcessedInvoiceNumberAfterRun}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void ApplyLogSearchFilter()
    {
        var filter = _historyLogSearchBox.Text;
        var lines = string.IsNullOrWhiteSpace(filter)
            ? _selectedRunLogLines
            : _selectedRunLogLines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _historyLogText.Text = string.Join(Environment.NewLine, lines);
    }
}
