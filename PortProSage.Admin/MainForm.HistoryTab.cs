using PortProSage.Admin.Models;
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

    // Shown next to Refresh so it's obvious how stale the grid currently is - History
    // & Logs no longer auto-refreshes while a run is active (see ResultPollTimer_Tick),
    // only when Refresh is clicked or a run genuinely finishes.
    private readonly Label _historyLastRefreshedLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private TabPage BuildResultsTab()
    {
        var page = new TabPage("History && Logs");

        SetupHistoryGrid();
        SetupOutcomesGrid();

        var refreshButton = new Button { Text = "Refresh" };
        refreshButton.Click += (_, _) => RefreshHistoryList();
        var refreshBar = CreateActionButtonBar(refreshButton, DockStyle.Top, barHeight: 40);
        _historyLastRefreshedLabel.Location = new Point(refreshButton.Right + 16, (refreshBar.Height - _historyLastRefreshedLabel.Height) / 2);
        refreshBar.Controls.Add(_historyLastRefreshedLabel);

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
        page.Controls.Add(refreshBar);

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
        _historyGrid.Columns.Add("Skipped", "Skipped");
        _historyGrid.Columns.Add("NotFound", "Not found");
        _historyGrid.Columns.Add("ZeroAmt", "Zero/-ve Amt");
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
        _historyGrid.Columns["Skipped"].Width = 80;
        _historyGrid.Columns["NotFound"].Width = 80;
        _historyGrid.Columns["ZeroAmt"].Width = 90;
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
        _historyOutcomesGrid.Columns.Add("PortProDate", "PortPro Date");
        _historyOutcomesGrid.Columns.Add("Success", "Success");
        _historyOutcomesGrid.Columns.Add("SageNumber", "Sage 50 #");
        _historyOutcomesGrid.Columns.Add("Messages", "Messages");

        // Proportional widths (FillWeight, not pixels) - AutoSizeColumnsMode.Fill is
        // already set on the grid itself (see field declaration above), so these sum
        // to 100 and read directly as percentages of the available width.
        _historyOutcomesGrid.Columns["Reference"].FillWeight = 16;
        _historyOutcomesGrid.Columns["PortProDate"].FillWeight = 14;
        _historyOutcomesGrid.Columns["Success"].FillWeight = 15;
        _historyOutcomesGrid.Columns["SageNumber"].FillWeight = 15;
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

    /// <summary>Derives (found, checked) for an InvoiceNumberList/gap-fill run.
    /// "checked" comes from ResolvedInvoiceNumberList's own candidate count, NOT
    /// from InvoicesFetched + InvoicesNotFound - that field didn't exist at all
    /// before 2026-08-15, so any run from before then has it stuck at 0 no matter
    /// how many candidates were actually missing. ResolvedInvoiceNumberList was
    /// already being recorded before that, so deriving "checked" from its count
    /// (and "not found" as checked-found) works uniformly for old and new runs,
    /// rather than only fixing the display going forward.</summary>
    private static (int Found, int Checked) GetGapFillCounts(SyncResult? result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.ResolvedInvoiceNumberList)) return (result?.InvoicesFetched ?? 0, 0);
        var checkedCount = result.ResolvedInvoiceNumberList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return (result.InvoicesFetched, checkedCount);
    }

    /// <summary>Display-only label for the Mode column - InvoiceNumberGapScan is
    /// never picked by hand (see GapFillRunner, Core-side), so its enum name reads
    /// as an internal implementation detail here rather than "what actually
    /// happened this row". Every other FilterType's enum name is already a
    /// reasonable label as-is.
    ///
    /// For a finished gap-fill run, appends "(found/checked)" - e.g. "(19/98)" -
    /// so the single most useful number (how many of the candidates it went and
    /// checked turned out to be real) is visible directly in the grid, not just
    /// buried in Summary. See GetGapFillCounts for why "checked" isn't just
    /// InvoicesFetched + InvoicesNotFound.</summary>
    private static string FormatModeText(FilterType filterType, SyncResult? result)
    {
        if (filterType != FilterType.InvoiceNumberGapScan) return filterType.ToString();
        if (result is null || !result.IsFinal) return "Finding the Gap";

        var (found, checkedCount) = GetGapFillCounts(result);
        return checkedCount > 0
            ? $"Finding the Gap ({found}/{checkedCount})"
            : "Finding the Gap";
    }

    private void RefreshHistoryList()
    {
        if (string.IsNullOrWhiteSpace(_triggerFolder) || string.IsNullOrWhiteSpace(_processedTriggerFolder)) return;

        _historyLastRefreshedLabel.Text = $"Last refreshed: {DateTime.Now:HH:mm:ss}";

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
                    ? (entry.Request.UseWatermark ? "Continue" : FormatModeText(entry.Request.FilterType, entry.Result))
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

            // Same found/checked framing as the Mode column's "(19/98)" and the
            // Summary panel's "Fetched: 0/79" - a bare "0" for a gap-fill run
            // reads as if nothing was even checked, when 79 candidates were. See
            // GetGapFillCounts for why this is derived from ResolvedInvoiceNumberList
            // rather than trusted from InvoicesNotFound directly - that field is 0
            // (not just unpopulated) on every run from before 2026-08-15.
            var (gapFound, gapChecked) = GetGapFillCounts(entry.Result);
            var fetchedText = entry.Result is null
                ? ""
                : gapChecked > 0
                    ? $"{gapFound}/{gapChecked}"
                    : entry.Result.InvoicesFetched.ToString();
            var notFoundText = entry.Result is null
                ? ""
                : gapChecked > 0
                    ? Math.Max(0, gapChecked - gapFound).ToString()
                    : entry.Result.InvoicesNotFound.ToString();

            var rowIndex = _historyGrid.Rows.Add(
                seqByRequestId.GetValueOrDefault(entry.RequestId, 0),
                entry.RequestId,
                source,
                mode,
                entry.Result?.StartedAtUtc.ToLocalTime().ToString("g") ?? entry.Request?.RequestedAtUtc.ToLocalTime().ToString("g") ?? "",
                finishedText,
                entry.Result?.EffectiveFromUtc?.ToLocalTime().ToString("g") ?? "",
                entry.Result?.EffectiveToUtc?.ToLocalTime().ToString("g") ?? "",
                fetchedText,
                entry.Result?.InvoicesImported.ToString() ?? "",
                entry.Result?.InvoicesSkippedAlreadyImported.ToString() ?? "",
                notFoundText,
                entry.Result?.InvoicesSkippedZeroOrNegativeAmount.ToString() ?? "",
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
                if (row.Cells["RequestId"].Value?.ToString() == selectedId)
                {
                    _historyGrid.CurrentCell = row.Cells[0];
                    row.Selected = true;
                    break;
                }
            }
        }

        // Rows.Clear()/Add() above transiently fires SelectionChanged with nothing
        // selected, which blanks the detail panes (ShowSelectedHistoryEntry) - re-
        // selecting a row via CurrentCell/Selected doesn't reliably re-fire that
        // event in every case, so call it directly to guarantee the panes match
        // whatever ended up selected. Confirmed live 2026-08-12: without this,
        // clicking Refresh left the detail view blank until switching to a
        // different row and back.
        ShowSelectedHistoryEntry();
    }

    /// <summary>Selects the most recent (topmost) row - the grid is always ordered
    /// newest-first (see RunHistoryService.ListRuns' OrderByDescending). Used
    /// whenever the view should default to the latest activity instead of
    /// preserving whatever was previously selected: switching to this tab, and
    /// right after starting a new run (its own entry becomes the top row as soon
    /// as the grid refreshes, since its RequestedAtUtc is newer than anything
    /// already completed).</summary>
    private void SelectTopHistoryRow()
    {
        if (_historyGrid.Rows.Count == 0) return;
        _historyGrid.ClearSelection();
        _historyGrid.CurrentCell = _historyGrid.Rows[0].Cells[0];
        _historyGrid.Rows[0].Selected = true;
        ShowSelectedHistoryEntry();
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

        // IsFinal, not just "a result file exists" - since SyncOrchestrator started
        // checkpointing progress after every invoice (see SyncResult.IsFinal's doc
        // comment), a result.json exists almost immediately after a run starts, long
        // before it's actually done. Checking existence alone (as this used to)
        // stopped polling - and this whole window's history display - at the FIRST
        // checkpoint, confirmed live 2026-08-10: the grid and Full log both froze on
        // an early mid-run snapshot and never updated again, even though the run kept
        // going and genuinely finished minutes later.
        var result = TriggerService.TryReadResult(_pendingProcessedFolder, _pendingRequestId);
        if (result is { IsFinal: true })
        {
            _pendingRequestId = null;
            _resultPollTimer.Stop();
            RefreshHistoryList();
            ResetRunFormToDefaults();
            ShowRunCompletionMessage(result);
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
            ShowRunCompletionMessage(result); // result here is non-null-but-not-final (a checkpoint), or null if the
                                               // process died before ever writing one - both handled below.
            return;
        }

        // Still running - deliberately NOT calling RefreshHistoryList() here anymore.
        // Confirmed live 2026-08-13: rebuilding the whole grid (a full folder rescan,
        // re-reading every historical result file, not just this one) every 2 seconds
        // was wasted work with no real benefit - the header's activity indicator (see
        // RefreshServiceStatus) already shows something is running, and History & Logs
        // now only ever refreshes on demand (the Refresh button) or once, for real,
        // when this run actually finishes below. TryReadResult above still polls
        // (one cheap file read) purely to detect that finish, not to update the UI.
    }

    /// <summary>Shown once, right when a Manual Run's polling stops (see
    /// ResultPollTimer_Tick) - a clear pass/fail summary instead of leaving the
    /// operator to go dig through History & Logs to find out what happened.
    /// Confirmed live 2026-08-12 this was a real gap on a large (700+ invoice)
    /// production run - no indication at all of completion, success count, or
    /// where to look if something went wrong.</summary>
    private void ShowRunCompletionMessage(SyncResult? result)
    {
        if (result is null)
        {
            MessageBox.Show(this,
                "The run stopped without ever recording a result - it may have crashed immediately. Check the " +
                "Full Log tab (below, in History & Logs) for what happened.",
                "Run did not complete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var hasFailures = result.InvoicesFailedValidation > 0 || result.InvoicesFailedImport > 0;

        if (!result.IsFinal)
        {
            MessageBox.Show(this,
                "Run was INTERRUPTED before finishing - the process stopped unexpectedly (crashed, was force-" +
                "stopped, or hit a fatal Sage 50 write error).\n\n" +
                $"As of its last checkpoint:\n" +
                $"Imported: {result.InvoicesImported}\n" +
                $"Failed validation: {result.InvoicesFailedValidation}\n" +
                $"Failed write: {result.InvoicesFailedImport}\n\n" +
                "Check the Failed Transactions tab or Full Log (below, in History & Logs) for exactly what happened - " +
                "re-running will pick up from where this left off, nothing already imported will be repeated.",
                "Run interrupted", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (hasFailures)
        {
            MessageBox.Show(this,
                "Run finished WITH ERRORS.\n\n" +
                $"Imported: {result.InvoicesImported}\n" +
                $"Already imported (skipped): {result.InvoicesSkippedAlreadyImported}\n" +
                $"Not found: {result.InvoicesNotFound}\n" +
                $"Failed validation: {result.InvoicesFailedValidation}\n" +
                $"Failed write: {result.InvoicesFailedImport}\n\n" +
                "Check the Failed Transactions tab or Full Log (below, in History & Logs) for exactly what went wrong.",
                "Run finished with errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show(this,
                "Run completed successfully.\n\n" +
                $"Imported {result.InvoicesImported} invoice(s) from PortPro to Sage 50.\n" +
                $"Already imported (skipped): {result.InvoicesSkippedAlreadyImported}\n" +
                $"Not found: {result.InvoicesNotFound}\n" +
                $"Zero/negative amount (skipped): {result.InvoicesSkippedZeroOrNegativeAmount}\n" +
                $"Before cutoff date (skipped): {result.InvoicesSkippedBeforeCutoff}",
                "Run complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
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

        // Extracted regardless of whether entry.Result exists - a run that started
        // but never got a result.json (crashed, or was killed before it could write
        // one) still has real log lines sitting in the Service's log file explaining
        // what happened, and that's exactly the case where seeing them matters most.
        // Computed BEFORE the Summary/outcomes grid below, since it's now also their
        // fallback source when result.json's own Outcomes list is missing/empty -
        // true for every automatic-poll entry (Outcomes is never populated for those,
        // only the summary counts) and for any run whose checkpoint file was lost.
        var selectedIndex = _historyGrid.SelectedRows[0].Index;
        var window = GetLogWindow(entry, selectedIndex);
        if (window is not null && !string.IsNullOrWhiteSpace(_logFolder))
        {
            _selectedRunLogLines = LogExtractorService.ExtractForWindow(_logFolder, window.Value.Start, window.Value.End);
        }

        _historySummaryText.Text = BuildSummaryText(entry, _selectedRunLogLines);

        if (entry.Result?.Outcomes is { Count: > 0 } outcomes)
        {
            foreach (var outcome in outcomes)
            {
                _historyOutcomesGrid.Rows.Add(
                    outcome.ReferenceNumber,
                    outcome.PortProInvoiceDate?.ToString("yyyy-MM-dd") ?? "",
                    outcome.Success ? "Yes" : "No",
                    outcome.Sage50InvoiceNumber ?? "",
                    string.Join(" | ", outcome.Messages));
            }
        }
        else
        {
            // result.json is missing, or came back with no Outcomes at all - most likely
            // because the checkpoint file itself was lost/corrupted (e.g. a disk-full
            // write), not because nothing was actually processed. Fall back to the log's
            // own "OUTCOME: ..." lines (written live, per invoice - see SyncOrchestrator.
            // RunAsync), same recovery path "Invoice Transferred" already had.
            foreach (var row in LogExtractorService.ExtractOutcomes(_selectedRunLogLines))
            {
                _historyOutcomesGrid.Rows.Add(row.ReferenceNumber, row.PortProDate, row.Success ? "Yes" : "No", row.Sage50InvoiceNumber, row.Messages);
            }
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

    private static string BuildSummaryText(RunHistoryEntry entry, List<string> logLines)
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

            // The actual reference-number list this run used - for a manually-typed
            // Invoice number list this just echoes it back; for a gap scan, this IS
            // the computed candidate list (gap scan rewrites itself into Invoice
            // number list internally - see SyncOrchestrator.RunAsync), so it's the
            // only place the real "what did the scan actually decide to check" list
            // is visible, not just documented in the log.
            if (!string.IsNullOrWhiteSpace(entry.Result.ResolvedInvoiceNumberList))
            {
                var candidateCount = entry.Result.ResolvedInvoiceNumberList
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
                lines.Add($"Invoice number list used ({candidateCount} candidate(s)): {entry.Result.ResolvedInvoiceNumberList}");
            }
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

            // For an InvoiceNumberList/gap-fill run, "Fetched: 0" alone reads as if
            // nothing was even checked - show it as "found/checked" (e.g. "0/79")
            // instead, same found/checked framing as the Mode column's "(19/98)",
            // so it's clear 79 candidates WERE checked and simply weren't found. See
            // GetGapFillCounts for why this is derived from ResolvedInvoiceNumberList
            // rather than trusted from InvoicesNotFound directly - that field reads
            // 0 (not just unpopulated) on every run from before 2026-08-15.
            var (summaryFound, summaryChecked) = GetGapFillCounts(entry.Result);
            var fetchedText = summaryChecked > 0
                ? $"{summaryFound}/{summaryChecked}"
                : entry.Result.InvoicesFetched.ToString();
            var notFoundCount = summaryChecked > 0 ? Math.Max(0, summaryChecked - summaryFound) : entry.Result.InvoicesNotFound;

            lines.Add("");
            lines.Add($"Fetched: {fetchedText}");
            lines.Add($"Imported (real writes this run): {entry.Result.InvoicesImported}");
            lines.Add($"Already imported (skipped): {entry.Result.InvoicesSkippedAlreadyImported}");
            lines.Add($"Not found: {notFoundCount}");
            lines.Add($"Zero/negative amount (skipped): {entry.Result.InvoicesSkippedZeroOrNegativeAmount}");
            lines.Add($"Before cutoff invoice date (skipped): {entry.Result.InvoicesSkippedBeforeCutoff}");
            lines.Add($"Failed validation: {entry.Result.InvoicesFailedValidation}");
            lines.Add($"Failed write: {entry.Result.InvoicesFailedImport}");
            lines.Add("");

            // Pre/post snapshot of the persisted "continue from" DATE - Start == End
            // confirms an explicit-range run genuinely left it untouched (the
            // guarantee UseWatermark=False is supposed to make); for a
            // watermark-driven run, the gap between them is exactly how far this
            // run advanced. See the Watermark tab for the current persisted value
            // directly - the invoice-number pair below is deliberately just what
            // THIS run actually touched, not the persisted watermark, to avoid
            // showing two different-looking "watermark" invoice numbers side by
            // side (the persisted one barely ever changes for most run types,
            // which read as a confusing mismatch against what was actually
            // processed).
            lines.Add($"Start Watermark (date): {entry.Result.WatermarkBeforeRun?.ToLocalTime().ToString("G") ?? "(none yet)"}");
            lines.Add($"End Watermark (date):   {entry.Result.WatermarkAfterRun?.ToLocalTime().ToString("G") ?? "(none yet)"}");

            // entry.Result.Outcomes directly when it's actually populated (a Manual
            // Run's own result.json) - falls back to the log's "OUTCOME: ..." lines
            // (LogExtractorService.ExtractOutcomes) otherwise, since Outcomes is never
            // populated for automatic-poll entries at all (only the summary counts
            // are), and can also be empty for any run whose checkpoint file was lost
            // (e.g. a disk-full write) - confirmed live 2026-08-13 this left Start/End
            // Invoice # permanently blank for both of those cases.
            var referenceNumbers = (entry.Result.Outcomes.Count > 0
                    ? entry.Result.Outcomes.Select(o => o.ReferenceNumber)
                    : LogExtractorService.ExtractOutcomes(logLines).Select(o => o.ReferenceNumber))
                .Where(r => !string.IsNullOrEmpty(r))
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();
            lines.Add($"Start Invoice #: {(referenceNumbers.Count > 0 ? referenceNumbers[0] : "(none)")}");
            lines.Add($"End Invoice #:   {(referenceNumbers.Count > 0 ? referenceNumbers[^1] : "(none)")}");
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
