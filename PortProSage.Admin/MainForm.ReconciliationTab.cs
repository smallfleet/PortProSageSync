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
    // most directly tied to "what happened, and when").
    private readonly CheckBox _reconciliationProcessDateEnabled = new() { Text = "Process Start/End:", AutoSize = true };
    private readonly DateTimePicker _reconciliationProcessFrom = new()
    { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 145, Enabled = false };
    private readonly DateTimePicker _reconciliationProcessTo = new()
    { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", Width = 145, Enabled = false };

    private readonly CheckBox _reconciliationInvoiceDateEnabled = new() { Text = "PortPro Invoice Date:", AutoSize = true };
    private readonly DateTimePicker _reconciliationInvoiceDateFrom = new()
    { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 110, Enabled = false };
    private readonly DateTimePicker _reconciliationInvoiceDateTo = new()
    { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 110, Enabled = false };

    private readonly CheckBox _reconciliationMismatchOnly = new() { Text = "Amount/Tax not matching", AutoSize = true };
    private readonly Label _reconciliationStatusLabel = new() { AutoSize = true, Text = "Not scanned yet - click Search/Refresh." };

    /// <summary>One row plus the extra context (which Sage50 path/run it came from)
    /// that Invoice Transferred's own per-run grid doesn't need to track, but this
    /// cross-run view does in order to filter by them.</summary>
    private List<(TransferredInvoiceRow Row, string? Sage50Path, DateTimeOffset ProcessStart, DateTimeOffset ProcessEnd)> _reconciliationAllRows = new();

    private TabPage BuildReconciliationTab()
    {
        var page = new TabPage("Reconciliation");
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
            _reconciliationProcessDateEnabled, _reconciliationProcessFrom,
            new Label { Text = "to", AutoSize = true }, _reconciliationProcessTo));
        filterPanel.Controls.Add(Group(
            _reconciliationInvoiceDateEnabled, _reconciliationInvoiceDateFrom,
            new Label { Text = "to", AutoSize = true }, _reconciliationInvoiceDateTo));
        filterPanel.Controls.Add(Group(_reconciliationMismatchOnly));

        var refreshButton = new Button { Text = "Search / Refresh" };
        refreshButton.Click += (_, _) => RunReconciliationScan();
        filterPanel.Controls.Add(Group(refreshButton, _reconciliationStatusLabel));

        _reconciliationProcessDateEnabled.CheckedChanged += (_, _) =>
        {
            _reconciliationProcessFrom.Enabled = _reconciliationProcessDateEnabled.Checked;
            _reconciliationProcessTo.Enabled = _reconciliationProcessDateEnabled.Checked;
            ApplyReconciliationFilters();
        };
        _reconciliationInvoiceDateEnabled.CheckedChanged += (_, _) =>
        {
            _reconciliationInvoiceDateFrom.Enabled = _reconciliationInvoiceDateEnabled.Checked;
            _reconciliationInvoiceDateTo.Enabled = _reconciliationInvoiceDateEnabled.Checked;
            ApplyReconciliationFilters();
        };
        _reconciliationProcessFrom.ValueChanged += (_, _) => ApplyReconciliationFilters();
        _reconciliationProcessTo.ValueChanged += (_, _) => ApplyReconciliationFilters();
        _reconciliationInvoiceDateFrom.ValueChanged += (_, _) => ApplyReconciliationFilters();
        _reconciliationInvoiceDateTo.ValueChanged += (_, _) => ApplyReconciliationFilters();
        _reconciliationMismatchOnly.CheckedChanged += (_, _) => ApplyReconciliationFilters();
        _reconciliationInvoiceSearch.TextChanged += (_, _) => ApplyReconciliationFilters();
        _reconciliationPathDropdown.SelectedIndexChanged += (_, _) => ApplyReconciliationFilters();

        page.Controls.Add(_reconciliationGrid);
        page.Controls.Add(filterPanel);

        RefreshAllTabsFromConfig += () => RefreshReconciliationPathDropdown();
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == page) RefreshReconciliationPathDropdown();
        };

        return page;
    }

    /// <summary>Same add/preserve-selection rules as History &amp; Logs' identical
    /// picker (RefreshHistoryPathDropdown) - nothing known yet shows a placeholder;
    /// the currently configured path is added if missing; an existing selection is
    /// never force-changed.</summary>
    private void RefreshReconciliationPathDropdown()
    {
        var knownPaths = Sage50PathStateService.GetAllKnownPaths(_syncStateDatabasePath.Text);
        var currentPath = _localSettings?.GetString("PortProSage.Sage50.CompanyDataPath")
            ?? _appSettings?.GetString("PortProSage.Sage50.CompanyDataPath");

        if (!string.IsNullOrWhiteSpace(currentPath) && !knownPaths.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
        {
            knownPaths.Insert(0, currentPath);
        }

        var previouslySelected = _reconciliationPathDropdown.SelectedItem as string;

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
        // "All paths" - reconciliation is an audit view, so seeing everything
        // together by default (unlike History & Logs, which follows the
        // currently-configured path first) is the more useful starting point.
        _reconciliationPathDropdown.Items.Add(AllPathsPlaceholder);
        foreach (var p in knownPaths) _reconciliationPathDropdown.Items.Add(p);

        if (previouslySelected is not null && _reconciliationPathDropdown.Items.Contains(previouslySelected))
        {
            _reconciliationPathDropdown.SelectedItem = previouslySelected;
        }
        else
        {
            _reconciliationPathDropdown.SelectedIndex = 0;
        }

        _reconciliationPathDropdown.SelectedIndexChanged += OnReconciliationPathDropdownRebuilt;
    }

    private const string AllPathsPlaceholder = "(all Sage50 paths)";

    private void OnReconciliationPathDropdownRebuilt(object? sender, EventArgs e) => ApplyReconciliationFilters();

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

            if (_reconciliationProcessDateEnabled.Checked &&
                (entry.ProcessStart < _reconciliationProcessFrom.Value || entry.ProcessStart > _reconciliationProcessTo.Value))
            {
                return false;
            }

            if (_reconciliationInvoiceDateEnabled.Checked)
            {
                if (!DateTimeOffset.TryParse(entry.Row.PortProDate, out var invoiceDate)) return false;
                if (invoiceDate.Date < _reconciliationInvoiceDateFrom.Value.Date || invoiceDate.Date > _reconciliationInvoiceDateTo.Value.Date)
                {
                    return false;
                }
            }

            if (_reconciliationMismatchOnly.Checked)
            {
                var amountMismatch = Math.Abs(entry.Row.TotalAmount - entry.Row.Sage50TotalAmount) > 0.01m;
                var taxMismatch = Math.Abs(entry.Row.TaxCharged - entry.Row.Sage50TaxCharged) > 0.01m;
                if (!amountMismatch && !taxMismatch) return false;
            }

            return true;
        }).ToList();

        foreach (var entry in filtered)
        {
            AddTransferredRow(_reconciliationGrid, entry.Row);
        }

        _reconciliationStatusLabel.Text =
            $"{filtered.Count} of {_reconciliationAllRows.Count} shown - last scanned {DateTime.Now:HH:mm:ss}";
    }
}
