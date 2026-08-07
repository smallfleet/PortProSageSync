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

        var topPanel = new Panel { Dock = DockStyle.Fill };
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
        detailTabs.TabPages.Add(warningsPage);
        detailTabs.TabPages.Add(failedPage);
        detailTabs.TabPages.Add(logPage);
        detailTabs.TabPages.Add(transferredPage);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
        split.Panel1.Controls.Add(topPanel);
        split.Panel2.Controls.Add(detailTabs);

        page.Controls.Add(split);
        page.Controls.Add(refreshButton);

        _historyGrid.SelectionChanged += (_, _) => ShowSelectedHistoryEntry();

        return page;
    }

    private void SetupHistoryGrid()
    {
        // Explicit widths, not a uniform Fill across every column - only the
        // genuinely long string column (Request ID) should be wide; date columns
        // are sized to fit an actual date/time string, and count columns are
        // sized to fit a small number, not stretched for no reason.
        _historyGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

        _historyGrid.Columns.Add("RequestId", "Request ID");
        _historyGrid.Columns.Add("Source", "Source");
        _historyGrid.Columns.Add("Mode", "Mode");
        _historyGrid.Columns.Add("Started", "Started");
        _historyGrid.Columns.Add("Finished", "Finished");
        _historyGrid.Columns.Add("Fetched", "Fetched");
        _historyGrid.Columns.Add("Imported", "Imported");
        _historyGrid.Columns.Add("Skipped", "Already imported");
        _historyGrid.Columns.Add("FailedVal", "Failed validation");
        _historyGrid.Columns.Add("FailedImp", "Failed write");
        _historyGrid.Columns.Add("Status", "Status");

        _historyGrid.Columns["Source"].Width = 110;
        _historyGrid.Columns["Mode"].Width = 130;
        _historyGrid.Columns["Started"].Width = 130;
        _historyGrid.Columns["Finished"].Width = 130;
        _historyGrid.Columns["Fetched"].Width = 60;
        _historyGrid.Columns["Imported"].Width = 65;
        _historyGrid.Columns["Skipped"].Width = 100;
        _historyGrid.Columns["FailedVal"].Width = 100;
        _historyGrid.Columns["FailedImp"].Width = 90;
        _historyGrid.Columns["Status"].Width = 100;

        // Request ID (a long GUID-like string) is the one column that should
        // absorb whatever width is left over, not every column stretching
        // proportionally.
        _historyGrid.Columns["RequestId"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
    }

    private void SetupOutcomesGrid()
    {
        _historyOutcomesGrid.Columns.Add("Reference", "Invoice #");
        _historyOutcomesGrid.Columns.Add("Success", "Success");
        _historyOutcomesGrid.Columns.Add("SageNumber", "Sage 50 #");
        _historyOutcomesGrid.Columns.Add("Messages", "Messages");
    }

    private void SetupTransferredGrid()
    {
        // Built from the run's full log (see LogExtractorService.ExtractTransferredInvoices),
        // not from result.json's Outcomes - the automatic poll never writes a result.json,
        // so the log is the only record that exists for those runs.
        _historyTransferredGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _historyTransferredGrid.Columns.Add("PortProRef", "PortPro Invoice #");
        _historyTransferredGrid.Columns.Add("PortProDate", "PortPro Date");
        _historyTransferredGrid.Columns.Add("Sage50Number", "Sage 50 Invoice #");
        _historyTransferredGrid.Columns.Add("Sage50Date", "Sage 50 Date");
        _historyTransferredGrid.Columns.Add("TotalAmount", "Total Amount");
        _historyTransferredGrid.Columns.Add("TaxCharged", "Tax Charged");

        _historyTransferredGrid.Columns["PortProDate"].Width = 100;
        _historyTransferredGrid.Columns["Sage50Number"].Width = 120;
        _historyTransferredGrid.Columns["Sage50Date"].Width = 100;
        _historyTransferredGrid.Columns["TotalAmount"].Width = 100;
        _historyTransferredGrid.Columns["TaxCharged"].Width = 100;
        _historyTransferredGrid.Columns["TotalAmount"].DefaultCellStyle.Format = "N2";
        _historyTransferredGrid.Columns["TaxCharged"].DefaultCellStyle.Format = "N2";

        _historyTransferredGrid.Columns["PortProRef"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
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

        _historyEntries = RunHistoryService.ListRuns(_triggerFolder, _processedTriggerFolder, _logFolder, _manualRunFolder);
        _historyGrid.Rows.Clear();
        RefreshPreviousRunSection();

        foreach (var entry in _historyEntries)
        {
            var mode = entry.Request is not null
                ? (entry.Request.UseWatermark ? "Continue" : entry.Request.FilterType.ToString())
                : "(auto-poll)";
            var source = entry.ReconstructedFromLog ? "Automatic Service"
                : entry.IsManual ? "Manual Run"
                : "Trigger file";
            var status = !entry.IsPending ? "Completed"
                : entry.IsManual ? "Running"
                : "Pending (queued)";

            var rowIndex = _historyGrid.Rows.Add(
                entry.RequestId,
                source,
                mode,
                entry.Result?.StartedAtUtc.ToLocalTime().ToString("g") ?? entry.Request?.RequestedAtUtc.ToLocalTime().ToString("g") ?? "",
                entry.Result?.FinishedAtUtc.ToLocalTime().ToString("g") ?? "",
                entry.Result?.InvoicesFetched.ToString() ?? "",
                entry.Result?.InvoicesImported.ToString() ?? "",
                entry.Result?.InvoicesSkippedAlreadyImported.ToString() ?? "",
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

    private void ResultPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_pendingRequestId is null || _pendingProcessedFolder is null) { _resultPollTimer.Stop(); return; }

        var result = TriggerService.TryReadResult(_pendingProcessedFolder, _pendingRequestId);
        RefreshHistoryList();
        if (result is not null)
        {
            _pendingRequestId = null;
            _resultPollTimer.Stop();
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

            if (!string.IsNullOrWhiteSpace(_logFolder))
            {
                _selectedRunLogLines = LogExtractorService.ExtractForWindow(_logFolder, entry.Result.StartedAtUtc, entry.Result.FinishedAtUtc);
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

    private static string BuildSummaryText(RunHistoryEntry entry)
    {
        var lines = new List<string>
        {
            $"Request ID: {entry.RequestId}",
            entry.ReconstructedFromLog ? "Source: Automatic poll (reconstructed from the log - no request/result file exists for auto-poll runs)" :
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

        if (entry.Result is not null)
        {
            lines.Add("");
            lines.Add($"Started:  {entry.Result.StartedAtUtc.ToLocalTime():G}");
            lines.Add($"Finished: {entry.Result.FinishedAtUtc.ToLocalTime():G}");
            lines.Add($"Duration: {(entry.Result.FinishedAtUtc - entry.Result.StartedAtUtc).TotalSeconds:0.0} sec");
            lines.Add("");
            lines.Add($"Fetched: {entry.Result.InvoicesFetched}");
            lines.Add($"Imported (real writes this run): {entry.Result.InvoicesImported}");
            lines.Add($"Already imported (skipped): {entry.Result.InvoicesSkippedAlreadyImported}");
            lines.Add($"Zero/negative amount (skipped): {entry.Result.InvoicesSkippedZeroOrNegativeAmount}");
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
