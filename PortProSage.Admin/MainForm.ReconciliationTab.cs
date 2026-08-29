using System.Linq;
using PortProSage.Admin.Services;

namespace PortProSage.Admin;

/// <summary>"Reconciliation" tab - the exact same look/feel and columns as History
/// &amp; Logs' per-run "Invoice Transferred" grid (see MainForm.HistoryTab.cs's
/// SetupTransferredGridColumns/AddTransferredRow, shared by both), but scanning
/// EVERY run's log instead of just whichever one is currently selected, with
/// filters to narrow it down. Exists so a PortPro-Amt-vs-Sage50-Amt mismatch (the
/// exact class of bug that under-billed 30 real invoices $49,065.80 before the
/// 2026-08-28 multi-charge-set fix) is something an operator can go looking for
/// directly, ongoing, instead of only discovering it from an external
/// reconciliation like the one that first surfaced it.</summary>
public partial class MainForm
{
    private readonly DataGridView _reconciliationGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };

    private readonly ComboBox _reconciliationPathDropdown = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _reconciliationInvoiceSearch = new() { Width = 140 };

    // Process Start/End first, then PortPro Invoice date - requested 2026-08-28
    // (Process is "when this app actually ran", Invoice date is "what PortPro
    // says the invoice is dated" - Process reads first since it's the filter
    // most directly tied to "what happened, and when"). No enable checkbox
    // (removed 2026-08-28, same "always a concrete value" preference already
    // applied to the Automatic Sync watermark field). Process Start/End default
    // to today's midnight through right now - requested 2026-08-28 - so the tab
    // opens scoped to "what happened today"; Clear Search resets back to this
    // same pair (see the Clear Search button below).
    private readonly DateTimePicker _reconciliationProcessFrom = new()
    { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 145, Value = DateTime.Today };
    private readonly DateTimePicker _reconciliationProcessTo = new()
    { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 145, Value = DateTime.Now };

    private readonly DateTimePicker _reconciliationInvoiceDateFrom = new()
    { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 110, Value = DateTime.Today.AddMonths(-6) };
    private readonly DateTimePicker _reconciliationInvoiceDateTo = new()
    { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 110, Value = DateTime.Today.AddDays(1) };

    // Red font (requested 2026-08-28) so this reads as the "show me the errors"
    // control at a glance, same visual language as the mismatched rows themselves.
    private readonly CheckBox _reconciliationMismatchOnly = new() { Text = "Amount/Tax not matching", AutoSize = true, ForeColor = Color.Red };
    private readonly Label _reconciliationStatusLabel = new() { AutoSize = true, Text = "Not scanned yet - click Search/Refresh." };

    /// <summary>One row plus the extra context (which Sage50 path/run it came from)
    /// that Invoice Transferred's own per-run grid doesn't need to track, but this
    /// cross-run view does in order to filter by them.</summary>
    private List<(TransferredInvoiceRow Row, string? Sage50Path, DateTimeOffset ProcessStart, DateTimeOffset ProcessEnd)> _reconciliationAllRows = new();

    private TabPage BuildReconciliationTab()
    {
        var page = new TabPage("Reconciliation");
        // Process Start/End lead as columns 1 and 2 (requested 2026-08-28) - this
        // grid spans every run, unlike Invoice Transferred's single-run view, so
        // which run each row came from needs to be visible in the grid itself, not
        // just in the filter above it.
        _reconciliationGrid.Columns.Add("ProcessStartDate", "Prcs Start Dt");
        _reconciliationGrid.Columns.Add("ProcessEndDate", "Prcs End Dt");
        _reconciliationGrid.Columns["ProcessStartDate"].FillWeight = 9;
        _reconciliationGrid.Columns["ProcessEndDate"].FillWeight = 9;
        SetupTransferredGridColumns(_reconciliationGrid);

        var filterPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            Padding = new Padding(8)
        };

        FlowLayoutPanel Group(params Control[] controls)
        {
            var group = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                Margin = new Padding(0, 0, 24, 8)
            };
            foreach (var c in controls)
            {
                c.Margin = new Padding(4, 6, 4, 0);
                group.Controls.Add(c);
            }
            return group;
        }

        filterPanel.Controls.Add(Group(new Label { Text = "Sage50 path:", AutoSize = true }, _reconciliationPathDropdown));
        filterPanel.Controls.Add(Group(new Label { Text = "Invoice #:", AutoSize = true }, _reconciliationInvoiceSearch));
        filterPanel.Controls.Add(Group(
            new Label { Text = "Process Start/End:", AutoSize = true }, _reconciliationProcessFrom,
            new Label { Text = "to", AutoSize = true }, _reconciliationProcessTo));
        filterPanel.Controls.Add(Group(
            new Label { Text = "PortPro Invoice Date:", AutoSize = true }, _reconciliationInvoiceDateFrom,
            new Label { Text = "to", AutoSize = true }, _reconciliationInvoiceDateTo));
        filterPanel.Controls.Add(Group(_reconciliationMismatchOnly));

        // Blue/white, matching History & Logs' "Delete Selected" red-button
        // treatment for a primary action - requested 2026-08-28, plain "Search"
        // (the "/Refresh" dropped) since it's one action either way.
        var searchButton = new Button
        {
            Text = "Search",
            Width = 90,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            BackColor = Color.FromArgb(0, 102, 204),
            ForeColor = Color.White
        };
        searchButton.FlatAppearance.BorderSize = 0;
        searchButton.Click += (_, _) => RunReconciliationScan();
        filterPanel.Controls.Add(Group(searchButton));

        // Resets every filter except Sage50 path (requested 2026-08-28 - the path
        // is treated as "which file am I working in", not part of the search
        // itself) - clears the invoice number search, resets Process Start/End
        // back to today's midnight through right now (the same tab-init default),
        // widens PortPro Invoice Date back to its own default, and unchecks the
        // mismatch-only filter. Then runs a fresh search (not just a re-filter),
        // per "Clear filter should bring back search... and search".
        var clearSearchButton = new Button { Text = "Clear Search", Width = 100 };
        clearSearchButton.Click += (_, _) =>
        {
            _reconciliationInvoiceSearch.Text = "";
            _reconciliationProcessFrom.Value = DateTime.Today;
            _reconciliationProcessTo.Value = DateTime.Now;
            _reconciliationInvoiceDateFrom.Value = DateTime.Today.AddMonths(-6);
            _reconciliationInvoiceDateTo.Value = DateTime.Today.AddDays(1);
            _reconciliationMismatchOnly.Checked = false;
            RunReconciliationScan();
        };
        filterPanel.Controls.Add(Group(clearSearchButton, _reconciliationStatusLabel));

        // Deliberately NOT wired to auto-apply (requested 2026-08-28) - changing
        // any of the four date fields only updates the pending value; nothing
        // re-filters until the Search button is clicked, which picks up whatever
        // the fields currently hold.
        _reconciliationMismatchOnly.CheckedChanged += (_, _) => ApplyReconciliationFilters();
        _reconciliationInvoiceSearch.TextChanged += (_, _) => ApplyReconciliationFilters();
        // Path changes trigger a fresh Search/Refresh, not just a re-filter of
        // whatever's already loaded - requested 2026-08-28. Wired through
        // OnReconciliationPathDropdownRebuilt only (see RefreshReconciliationPathDropdown,
        // which unsubscribes/resubscribes it around its own programmatic rebuild)
        // so this fires for a genuine user-picked path, never while the dropdown
        // is being repopulated.

        page.Controls.Add(_reconciliationGrid);
        page.Controls.Add(filterPanel);

        RefreshAllTabsFromConfig += () => RefreshReconciliationPathDropdown();
        // Re-derives the Sage50 path filter from the most recent run every time
        // this tab is selected (requested 2026-08-28) - not just once at startup -
        // so switching back to Reconciliation always reflects whatever was
        // actually just run, even if that's a different path than last time.
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab != page) return;
            if (!string.IsNullOrWhiteSpace(_triggerFolder) && !string.IsNullOrWhiteSpace(_processedTriggerFolder))
            {
                _historyEntries = RunHistoryService.ListRuns(_triggerFolder, _processedTriggerFolder, _logFolder, _manualRunFolder, _autoPollFolder);
            }
            RefreshReconciliationPathDropdown(forceLastRunPath: true);
            RunReconciliationScan();
        };

        return page;
    }

    /// <summary>Same add/preserve-selection rules as History &amp; Logs' identical
    /// picker (RefreshHistoryPathDropdown) - nothing known yet shows a placeholder;
    /// the currently configured path is added if missing; an existing selection is
    /// never force-changed (unless forceLastRunPath). Default preference order:
    /// the most recent run's actual Sage50Path (requested 2026-08-28 - this is an
    /// audit tool, so "what actually just ran" beats "what's currently configured
    /// but maybe not run yet"), then the Sage 50 tab's current setting, then
    /// whatever's first in the list.</summary>
    private void RefreshReconciliationPathDropdown(bool forceLastRunPath = false)
    {
        var knownPaths = Sage50PathStateService.GetAllKnownPaths(_syncStateDatabasePath.Text);
        var currentPath = _localSettings?.GetString("PortProSage.Sage50.CompanyDataPath")
            ?? _appSettings?.GetString("PortProSage.Sage50.CompanyDataPath");
        // _historyEntries is newest-first (RunHistoryService.ListRuns) - the first
        // entry with a recorded Sage50Path is the most recent run's.
        var lastRunPath = _historyEntries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Result?.Sage50Path))?.Result?.Sage50Path;

        if (!string.IsNullOrWhiteSpace(currentPath) && !knownPaths.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
        {
            knownPaths.Insert(0, currentPath);
        }
        if (!string.IsNullOrWhiteSpace(lastRunPath) && !knownPaths.Contains(lastRunPath, StringComparer.OrdinalIgnoreCase))
        {
            knownPaths.Insert(0, lastRunPath);
        }

        var previouslySelected = forceLastRunPath ? null : _reconciliationPathDropdown.SelectedItem as string;

        _reconciliationPathDropdown.SelectedIndexChanged -= OnReconciliationPathDropdownRebuilt;
        _reconciliationPathDropdown.Items.Clear();

        if (knownPaths.Count == 0)
        {
            _reconciliationPathDropdown.Items.Add(NoPathDefinedPlaceholder);
            _reconciliationPathDropdown.SelectedIndex = 0;
            _reconciliationPathDropdown.Enabled = false;
            _reconciliationPathDropdown.SelectedIndexChanged += OnReconciliationPathDropdownRebuilt;
            return;
        }

        _reconciliationPathDropdown.Enabled = true;
        // "(all paths)" still offered as an explicit choice, but not the default -
        // see this method's doc comment for the preference order.
        _reconciliationPathDropdown.Items.Add(AllPathsPlaceholder);
        foreach (var p in knownPaths) _reconciliationPathDropdown.Items.Add(p);

        if (previouslySelected is not null && _reconciliationPathDropdown.Items.Contains(previouslySelected))
        {
            _reconciliationPathDropdown.SelectedItem = previouslySelected;
        }
        else if (!string.IsNullOrWhiteSpace(lastRunPath) && _reconciliationPathDropdown.Items.Contains(lastRunPath))
        {
            _reconciliationPathDropdown.SelectedItem = lastRunPath;
        }
        else if (!string.IsNullOrWhiteSpace(currentPath) && _reconciliationPathDropdown.Items.Contains(currentPath))
        {
            _reconciliationPathDropdown.SelectedItem = currentPath;
        }
        else
        {
            _reconciliationPathDropdown.SelectedIndex = 0;
        }

        _reconciliationPathDropdown.SelectedIndexChanged += OnReconciliationPathDropdownRebuilt;
    }

    private const string AllPathsPlaceholder = "(all Sage50 paths)";

    private void OnReconciliationPathDropdownRebuilt(object? sender, EventArgs e) => RunReconciliationScan();

    /// <summary>Same as History &amp; Logs' AddTransferredRow, plus the two leading
    /// Process Start/Process End columns this grid alone has.</summary>
    private static void AddReconciliationRow(DataGridView grid, TransferredInvoiceRow row, DateTimeOffset processStart, DateTimeOffset processEnd)
    {
        var rowIndex = grid.Rows.Add(
            processStart.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            processEnd.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            row.PortProCustomerName, row.PortProReference, row.PortProDate,
            row.TotalAmount, row.TaxCharged, row.Sage50CustomerAction, row.Sage50InvoiceNumber, row.Sage50Date,
            string.IsNullOrEmpty(row.DueDate) ? "(n/a - pre-2026-08-22 run)" : row.DueDate,
            row.Sage50TotalAmount, row.Sage50TaxCharged);
        ApplyTransferredRowStyle(grid.Rows[rowIndex], row);
    }

    /// <summary>Scans every completed run's log for TRANSFER lines - this is the
    /// expensive step (re-reads each run's own daily log file), so it only runs on
    /// an explicit Search/Refresh click, not automatically on every tab visit or
    /// filter change; ApplyReconciliationFilters (cheap - in-memory only) handles
    /// everything after that.</summary>
    private void RunReconciliationScan()
    {
        if (string.IsNullOrWhiteSpace(_triggerFolder) || string.IsNullOrWhiteSpace(_processedTriggerFolder)) return;

        _reconciliationStatusLabel.Text = "Scanning...";
        Cursor = Cursors.WaitCursor;
        try
        {
            _historyEntries = RunHistoryService.ListRuns(_triggerFolder, _processedTriggerFolder, _logFolder, _manualRunFolder, _autoPollFolder);

            var rows = new List<(TransferredInvoiceRow Row, string? Sage50Path, DateTimeOffset ProcessStart, DateTimeOffset ProcessEnd)>();
            foreach (var entry in _historyEntries)
            {
                if (entry.Result is null) continue;
                var window = GetLogWindow(entry);
                if (window is null) continue;

                var logLines = string.IsNullOrWhiteSpace(_logFolder)
                    ? new List<string>()
                    : LogExtractorService.ExtractForWindow(_logFolder, window.Value.Start, window.Value.End, window.Value.HardLowerBound, window.Value.HardUpperBound);

                foreach (var row in LogExtractorService.ExtractTransferredInvoices(logLines))
                {
                    rows.Add((row, entry.Result.Sage50Path, entry.Result.StartedAtUtc, entry.Result.FinishedAtUtc));
                }
            }

            _reconciliationAllRows = rows;
            RefreshReconciliationPathDropdown();
            ApplyReconciliationFilters();
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void ApplyReconciliationFilters()
    {
        _reconciliationGrid.Rows.Clear();

        var selectedPath = _reconciliationPathDropdown.SelectedItem as string;
        var pathFilterActive = !string.IsNullOrEmpty(selectedPath) &&
            selectedPath != NoPathDefinedPlaceholder && selectedPath != AllPathsPlaceholder;

        var refSearch = _reconciliationInvoiceSearch.Text.Trim();

        var filtered = _reconciliationAllRows.Where(entry =>
        {
            // Entries that predate Sage50Path tracking always show, same reasoning
            // as History & Logs' identical MatchesHistoryPathFilter.
            if (pathFilterActive && entry.Sage50Path is not null &&
                !string.Equals(entry.Sage50Path, selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (refSearch.Length > 0 &&
                entry.Row.PortProReference.IndexOf(refSearch, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            // Both date ranges are always-active filters now (no enable checkbox -
            // removed 2026-08-28), defaulted wide enough on load to include
            // everything until the operator narrows them.
            if (entry.ProcessStart < _reconciliationProcessFrom.Value || entry.ProcessStart > _reconciliationProcessTo.Value)
            {
                return false;
            }

            if (!DateTimeOffset.TryParse(entry.Row.PortProDate, out var invoiceDate)) return false;
            if (invoiceDate.Date < _reconciliationInvoiceDateFrom.Value.Date || invoiceDate.Date > _reconciliationInvoiceDateTo.Value.Date)
            {
                return false;
            }

            if (_reconciliationMismatchOnly.Checked)
            {
                var amountMismatch = Math.Abs(entry.Row.TotalAmount - entry.Row.Sage50TotalAmount) > 0.01m;
                var taxMismatch = Math.Abs(entry.Row.TaxCharged - entry.Row.Sage50TaxCharged) > 0.01m;
                if (!amountMismatch && !taxMismatch) return false;
            }

            return true;
        })
        // Sorted by Process Start date by default, most recent first - requested
        // 2026-08-28.
        .OrderByDescending(entry => entry.ProcessStart)
        .ToList();

        foreach (var entry in filtered)
        {
            AddReconciliationRow(_reconciliationGrid, entry.Row, entry.ProcessStart, entry.ProcessEnd);
        }

        _reconciliationStatusLabel.Text =
            $"{filtered.Count} of {_reconciliationAllRows.Count} shown - last scanned {DateTime.Now:HH:mm:ss}";
    }
}
