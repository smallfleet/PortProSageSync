using System.Diagnostics;
using System.Linq;
using PortProSage.Admin.Models;
using PortProSage.Admin.Services;

namespace PortProSage.Admin;

public partial class MainForm
{
    private DataGridView _customerRefreshGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = false,
        AllowUserToAddRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
    };

    // Top-left of the grid's own bar, per explicit request - same one-way
    // toggle idiom as History & Logs' own "Select all" (MainForm.HistoryTab.cs),
    // just positioned on the opposite side.
    private readonly CheckBox _customerRefreshSelectAllCheckbox = new() { Text = "Select all", AutoSize = true };

    // Toggles word-wrap on the two long free-text columns (PortPro/Sage 50
    // Details) - unchecked (the default) keeps rows compact/single-line;
    // checked wraps and grows each row to fit its full content.
    private readonly CheckBox _customerRefreshWrapDetails = new() { Text = "Wrapped Details", AutoSize = true };

    private readonly Button _customerRefreshScanButton = new() { Text = "Extract All Customer", Width = 170, Height = 30 };
    private readonly Button _customerRefreshRunButton = new() { Text = "Run Selected", Width = 150, Height = 34, Enabled = false };
    private readonly Label _customerRefreshLastScannedLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    // Incremental "find and jump to" - not a filter (nothing is hidden), just
    // scrolls to and highlights the first customer name containing what's typed.
    private readonly TextBox _customerRefreshSearchBox = new() { Width = 220 };

    // Overlaid on top of the grid (added after it, so it sits in front - see
    // BuildCustomerRefreshTab) whenever there's nothing to show yet, instead of
    // just a blank grid: before the first Extract, while one is in progress, and
    // if a completed extract genuinely found nothing.
    private readonly Label _customerRefreshEmptyLabel = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText,
        Font = new Font("Segoe UI", 11f),
        BackColor = Color.White,
        Text = "Press \"Extract All Customer\" to pull PortPro customers and their Sage 50 comparison."
    };

    // Always visible on this tab (not just inside the confirmation dialogs) so
    // the operator never loses track of which real Sage 50 company file is
    // about to be read from/written to - requested explicitly since this tab can
    // both create and overwrite real customer records.
    private readonly Label _customerRefreshTargetLabel = new()
    {
        Dock = DockStyle.Top,
        Height = 26,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(12, 0, 12, 0),
        ForeColor = Color.FromArgb(150, 20, 20),
        BackColor = Color.FromArgb(255, 244, 244)
    };

    // A SEPARATE, independent Dry Run - deliberately not the same field as
    // _runDryRun/_sage50DryRun (the shared PortProSage:Sage50:DryRun setting).
    // Defaults to UNCHECKED every time this tab is built (app start) - confirmed
    // 2026-08-24 - and ResetCustomerRefreshFormToDefaults() puts it back to
    // unchecked after every refresh (success or failure) - see that method's doc
    // comment. Never written to appsettings anywhere; see SyncRequest.
    // CustomerRefreshDryRun.
    private readonly CheckBox _customerRefreshDryRun = new()
    {
        Text = "Dry run (Simulated - no real Sage 50 Changes)",
        AutoSize = true,
        Checked = false
    };

    // Tracks GetServiceRunState() separately from row-selection, so
    // UpdateCustomerRefreshRunButtonText can combine both conditions (Run
    // Selected must stay disabled while something else is running, REGARDLESS
    // of how many rows are checked).
    private bool _customerRefreshServiceAvailable = true;

    private List<CustomerRefreshCandidate> _customerRefreshCandidates = new();

    private const string CustomerRefreshHelpText =
        "Two steps: Extract, then Run Selected. Nothing is pulled or shown automatically - the grid starts " +
        "empty every time this app opens.\n\n" +
        "1. \"Extract All Customer\" fetches every PortPro customer and checks each one against Sage 50 by " +
        "name - entirely read-only, nothing is written. Fills the grid below with one row per customer: whether " +
        "it would be INSERTED (no match in Sage 50) or UPDATED (a match exists), PortPro's incoming profile, and " +
        "- for an UPDATE row - Sage 50's current profile, side by side. Pressing it again re-extracts and " +
        "replaces the list.\n\n" +
        "2. Tick the rows you actually want to process (or use Select all), then click \"Run Selected\" - this " +
        "is the only step that writes anything, and only for the rows you checked.\n\n" +
        "Use the search box (top-right) to jump to a customer by name as you type - it scrolls to and " +
        "highlights the first match, without hiding any other row.\n\n" +
        "This is NOT the same as the \"Update Customer with latest changes in PortPro\" setting on the Sage 50 " +
        "tab - that one only pushes a customer whose PortPro profile changed since it was last synced, " +
        "automatically, in the background, once per Automatic Service cycle / Manual Run, and never creates. " +
        "This tab ignores that changed-since-last-sync check entirely, and CAN create a brand new Sage 50 " +
        "customer for an INSERT row (gated by \"Auto-create missing customers\" on the Sage 50 tab, same switch " +
        "used when a new customer shows up on an invoice).\n\n" +
        "Both Extract and Run Selected are disabled while the Automatic Service or a Manual Run is active - all " +
        "of them connect to Sage 50 under the same account, and Sage 50 rejects a second simultaneous session.\n\n" +
        "Both steps register their own row in History & Logs once finished (\"Customer Refresh (scan)\" / " +
        "\"Customer Refresh\") - check there for exactly what was found, created, updated, or failed.";

    private const string CustomerRefreshDryRunHelpText =
        "When checked, Run Selected only SIMULATES the create/update - nothing is actually changed in Sage 50, " +
        "the log just says what it would have done instead. Unchecked (the default) is a REAL write.\n\n" +
        "This is a SEPARATE, INDEPENDENT setting from the Dry run checkbox on Manual Run / Sage 50 tab - " +
        "toggling this one never affects that one, and vice versa.\n\n" +
        "Always starts UNCHECKED when this app opens, and resets itself back to unchecked after every Run " +
        "Selected - success or failure - so checking it for one test doesn't silently carry forward and simulate " +
        "a later real run you actually meant to write for real.\n\n" +
        "When checked, the confirmation dialog and every message about that run leads with \"*** DRY RUN ***\" " +
        "at the very top, so it's never mistaken for a real write.";

    private TabPage BuildCustomerRefreshTab()
    {
        var page = new TabPage("Customer Refresh");

        SetupCustomerRefreshGrid();

        // Everything on the left (button, label, checkboxes) lives in one
        // FlowLayoutPanel instead of being positioned by hand via Location/
        // SizeChanged chains between siblings - confirmed live 2026-08-24 that
        // approach is fragile: "Wrapped Details" was wired to reposition off
        // _customerRefreshSelectAllCheckbox.SizeChanged, but that checkbox's own
        // SIZE never actually changes (only its LOCATION does, when the label
        // before it grows) - so SizeChanged never fired, and Wrapped Details was
        // left stranded wherever it was first placed. A FlowLayoutPanel lays out
        // its children left-to-right automatically on every change, with no
        // manual math or event-wiring to get wrong.
        var scanBar = new Panel { Dock = DockStyle.Top, Height = 40 };

        _customerRefreshScanButton.AutoSize = false;
        _customerRefreshScanButton.Height = 30;
        _customerRefreshScanButton.Margin = new Padding(10, 5, 20, 0);
        _customerRefreshScanButton.BackColor = ActionButtonColor;
        _customerRefreshScanButton.ForeColor = Color.White;
        _customerRefreshScanButton.FlatStyle = FlatStyle.Flat;
        _customerRefreshScanButton.FlatAppearance.BorderSize = 0;
        _customerRefreshScanButton.Cursor = Cursors.Hand;
        _customerRefreshScanButton.Click += (_, _) => StartCustomerRefreshScan();

        _customerRefreshLastScannedLabel.Margin = new Padding(0, 12, 20, 0);

        _customerRefreshSelectAllCheckbox.Margin = new Padding(0, 12, 20, 0);
        _customerRefreshSelectAllCheckbox.CheckedChanged += (_, _) =>
        {
            foreach (DataGridViewRow row in _customerRefreshGrid.Rows)
            {
                row.Cells["Select"].Value = _customerRefreshSelectAllCheckbox.Checked;
            }
            _customerRefreshGrid.EndEdit();
            UpdateCustomerRefreshRunButtonText();
        };

        // Toggles word-wrap on the two Details columns - unchecked keeps rows a
        // single fixed-height line; checked wraps text and grows each row to fit.
        _customerRefreshWrapDetails.Margin = new Padding(0, 12, 0, 0);
        _customerRefreshWrapDetails.CheckedChanged += (_, _) =>
        {
            var wrap = _customerRefreshWrapDetails.Checked ? DataGridViewTriState.True : DataGridViewTriState.False;
            _customerRefreshGrid.Columns["PortProDetails"].DefaultCellStyle.WrapMode = wrap;
            _customerRefreshGrid.Columns["SageDetails"].DefaultCellStyle.WrapMode = wrap;

            if (_customerRefreshWrapDetails.Checked)
            {
                _customerRefreshGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            }
            else
            {
                _customerRefreshGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
                foreach (DataGridViewRow row in _customerRefreshGrid.Rows)
                {
                    row.Height = _customerRefreshGrid.RowTemplate.Height;
                }
            }
        };

        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false
        };
        leftFlow.Controls.Add(_customerRefreshScanButton);
        leftFlow.Controls.Add(_customerRefreshLastScannedLabel);
        leftFlow.Controls.Add(_customerRefreshSelectAllCheckbox);
        leftFlow.Controls.Add(_customerRefreshWrapDetails);

        // Incremental find - scrolls to and highlights the first matching
        // customer name as you type; doesn't hide/filter any row. Docked into its
        // own Right-docked sub-panel (not part of the left FlowLayoutPanel) so it
        // stays pinned to the right edge regardless of how long the left side's
        // content grows.
        _customerRefreshSearchBox.PlaceholderText = "Search customer name...";
        _customerRefreshSearchBox.Dock = DockStyle.Fill;
        _customerRefreshSearchBox.TextChanged += (_, _) => FindAndSelectCustomerRefreshRow(_customerRefreshSearchBox.Text);
        var searchPanel = new Panel { Dock = DockStyle.Right, Width = 240, Padding = new Padding(0, 8, 16, 8) };
        searchPanel.Controls.Add(_customerRefreshSearchBox);

        scanBar.Controls.Add(leftFlow);
        scanBar.Controls.Add(searchPanel);

        // Single click toggles the checkbox immediately - standard DataGridView
        // checkbox-column gotcha, same fix as History & Logs' own Select column.
        _customerRefreshGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_customerRefreshGrid.IsCurrentCellDirty && _customerRefreshGrid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _customerRefreshGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _customerRefreshGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && _customerRefreshGrid.Columns[e.ColumnIndex].Name == "Select")
            {
                UpdateCustomerRefreshRunButtonText();
            }
        };

        var gridPanel = new Panel { Dock = DockStyle.Fill };
        gridPanel.Controls.Add(_customerRefreshGrid);
        gridPanel.Controls.Add(_customerRefreshEmptyLabel); // added after the grid -> renders on top of it

        RefreshCustomerRefreshTargetLabel();
        RefreshAllTabsFromConfig += RefreshCustomerRefreshTargetLabel;

        // Also refresh on every click into this tab, not just on config load -
        // requested explicitly so the shown path is always current at the moment
        // it's actually looked at, even if the Sage 50 tab's path was edited
        // (but not yet saved through a full config reload) since this tab was
        // last visited.
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == page) RefreshCustomerRefreshTargetLabel();
        };

        _customerRefreshRunButton.Click += (_, _) => StartCustomerRefreshExecute();
        _customerRefreshRunButton.BackColor = Color.FromArgb(196, 43, 43);
        _customerRefreshRunButton.ForeColor = Color.White;
        _customerRefreshRunButton.FlatStyle = FlatStyle.Flat;
        _customerRefreshRunButton.FlatAppearance.BorderSize = 0;
        _customerRefreshRunButton.Cursor = Cursors.Hand;
        var customerRefreshHelp = CreateHelpIcon("Customer Refresh", CustomerRefreshHelpText);
        var dryRunHelp = CreateHelpIcon("Dry run", CustomerRefreshDryRunHelpText);

        var warning = new Label
        {
            Text = "⚠ RUN SELECTED WILL CREATE NEW CUSTOMERS IN SAGE 50 FOR ANY SELECTED ROW MARKED INSERT, AND " +
                   "OVERWRITE EXISTING CUSTOMER DATA FOR ANY ROW MARKED UPDATE, USING WHATEVER PORTPRO CURRENTLY " +
                   "HAS ON FILE. ANY CHANGES MADE DIRECTLY IN SAGE 50 TO AN UPDATED CUSTOMER WILL BE LOST.",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 32,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(150, 20, 20)
        };

        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 120, Padding = new Padding(12, 8, 12, 8) };
        _customerRefreshDryRun.Location = new Point(0, 4);
        dryRunHelp.Location = new Point(_customerRefreshDryRun.Right + 8, 2);
        _customerRefreshRunButton.Location = new Point(0, 76);
        customerRefreshHelp.Location = new Point(_customerRefreshRunButton.Right + 16, 82);
        var warningPanel = new Panel { Location = new Point(0, 30), Size = new Size(900, 36) };
        warningPanel.Controls.Add(warning);

        bottomBar.Controls.Add(_customerRefreshDryRun);
        bottomBar.Controls.Add(dryRunHelp);
        bottomBar.Controls.Add(warningPanel);
        bottomBar.Controls.Add(_customerRefreshRunButton);
        bottomBar.Controls.Add(customerRefreshHelp);

        _customerRefreshTargetLabel.Font = new Font(Font, FontStyle.Bold);

        page.Controls.Add(gridPanel);
        page.Controls.Add(bottomBar);
        page.Controls.Add(_customerRefreshTargetLabel);
        page.Controls.Add(scanBar);

        return page;
    }

    private void SetupCustomerRefreshGrid()
    {
        typeof(DataGridView).InvokeMember("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.SetProperty,
            null, _customerRefreshGrid, new object[] { true });

        _customerRefreshGrid.RowTemplate.Height = 24;
        _customerRefreshGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _customerRefreshGrid.ColumnHeadersHeight = 26;
        _customerRefreshGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

        _customerRefreshGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Select", HeaderText = "", Width = 30 });
        _customerRefreshGrid.Columns.Add("Seq", "#");
        _customerRefreshGrid.Columns.Add("CustomerName", "Customer Name");
        _customerRefreshGrid.Columns.Add("PortProDetails", "PortPro Details (incoming)");
        _customerRefreshGrid.Columns.Add("Operation", "Operation");
        _customerRefreshGrid.Columns.Add("Applied", "Applied");
        _customerRefreshGrid.Columns.Add("Date", "Date");
        _customerRefreshGrid.Columns.Add("SageCustomerName", "Sage Customer Name");
        _customerRefreshGrid.Columns.Add("SageDetails", "Sage 50 Details (current)");

        _customerRefreshGrid.Columns["Seq"].Width = 45;
        _customerRefreshGrid.Columns["CustomerName"].Width = 200;
        _customerRefreshGrid.Columns["Operation"].Width = 85;
        _customerRefreshGrid.Columns["Applied"].Width = 70;
        _customerRefreshGrid.Columns["Date"].Width = 130;
        _customerRefreshGrid.Columns["SageCustomerName"].Width = 200;

        // Two flexible text columns share the remaining space - both get Fill
        // (same "grid mode None, selective per-column Fill" idiom as History &
        // Logs' own Status column) so PortPro's and Sage 50's details read as a
        // genuine side-by-side comparison rather than one being squeezed.
        _customerRefreshGrid.Columns["PortProDetails"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _customerRefreshGrid.Columns["PortProDetails"].FillWeight = 50;
        _customerRefreshGrid.Columns["SageDetails"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _customerRefreshGrid.Columns["SageDetails"].FillWeight = 50;

        _customerRefreshGrid.ReadOnly = false;
        foreach (DataGridViewColumn column in _customerRefreshGrid.Columns)
        {
            if (column.Name != "Select") column.ReadOnly = true;
        }
    }

    /// <summary>Puts this tab's Dry Run back to its default (unchecked) - called
    /// once a Customer Refresh scan or execute finishes, whether it succeeded,
    /// finished with errors, or was interrupted (see ResultPollTimer_Tick,
    /// MainForm.HistoryTab.cs, and StopManualRun). Deliberately unconditional
    /// (called even after an ordinary Manual Run finishes) - resetting a field on
    /// a tab that wasn't even used this time is harmless, and it's simpler and
    /// more robust than threading a condition through every place a run can end.</summary>
    private void ResetCustomerRefreshFormToDefaults()
    {
        _customerRefreshDryRun.Checked = false;
    }

    // Cells from "Operation" onward get colored red for an INSERT row, per
    // explicit request - the word "INSERT" itself through the rest of that row,
    // not the Select/#/Customer Name/PortPro Details columns before it.
    private static readonly string[] InsertHighlightColumns = { "Operation", "Applied", "Date", "SageCustomerName", "SageDetails" };
    private static readonly Color InsertRed = Color.FromArgb(163, 38, 38);

    private void PopulateCustomerRefreshGrid(List<CustomerRefreshCandidate> candidates)
    {
        _customerRefreshCandidates = candidates;
        _customerRefreshGrid.Rows.Clear();
        _customerRefreshSelectAllCheckbox.Checked = false;

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var rowIndex = _customerRefreshGrid.Rows.Add(
                false, i + 1, c.CompanyName, c.PortProDetails, c.Operation, "", "",
                c.SageCustomerName ?? "(new)", c.SageDetails ?? "(does not exist in Sage 50)");
            var row = _customerRefreshGrid.Rows[rowIndex];
            row.Tag = c;

            if (c.Operation == "INSERT")
            {
                foreach (var colName in InsertHighlightColumns) row.Cells[colName].Style.ForeColor = InsertRed;
            }

            // Pre-fills Applied/Date from the persisted customer_refresh_status
            // table (see ScanForRefreshAsync) - the last time this customer was
            // actually run, even from a previous app session, not just this one.
            if (c.LastAppliedAtUtc is { } lastApplied)
            {
                SetAppliedCell(row, c.Operation, c.LastOperationSuccess ?? false, lastApplied);
            }
        }

        var insertCount = candidates.Count(c => c.Operation == "INSERT");
        var updateCount = candidates.Count(c => c.Operation == "UPDATE");
        _customerRefreshLastScannedLabel.Text =
            $"Last extracted: {DateTime.Now:HH:mm:ss} - {candidates.Count} customer(s) total ({insertCount} to insert, {updateCount} to update)";

        _customerRefreshEmptyLabel.Text = "No PortPro customers found.";
        _customerRefreshEmptyLabel.Visible = candidates.Count == 0;

        UpdateCustomerRefreshRunButtonText();
    }

    private void ClearCustomerRefreshGrid()
    {
        _customerRefreshCandidates = new List<CustomerRefreshCandidate>();
        _customerRefreshGrid.Rows.Clear();
        _customerRefreshSelectAllCheckbox.Checked = false;
        _customerRefreshEmptyLabel.Text = "Press \"Extract All Customer\" to pull PortPro customers and their Sage 50 comparison.";
        _customerRefreshEmptyLabel.Visible = true;
        UpdateCustomerRefreshRunButtonText();
    }

    /// <summary>Updates the ALREADY-VISIBLE rows in place with what actually
    /// happened - requested explicitly so Run Selected no longer clears the grid
    /// afterward (it used to). Matches each outcome back to its row by
    /// PortProCustomerId (not row index - order is stable but this is more
    /// robust), fills in Applied (Success/Failed) and Date, and color-codes
    /// Applied for an UPDATE row (an INSERT row already reads red end-to-end from
    /// PopulateCustomerRefreshGrid, so its own status is conveyed by the text,
    /// not a second color). Rows that weren't part of this run (not selected, or
    /// this was a scan) are left untouched.</summary>
    private void ApplyCustomerRefreshOutcomes(List<CustomerRefreshOutcome> outcomes)
    {
        if (outcomes.Count == 0) return;

        var byId = outcomes
            .GroupBy(o => o.PortProCustomerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (DataGridViewRow row in _customerRefreshGrid.Rows)
        {
            if (row.Tag is not CustomerRefreshCandidate c) continue;
            if (!byId.TryGetValue(c.PortProCustomerId, out var outcome)) continue;

            SetAppliedCell(row, c.Operation, outcome.Success, outcome.AppliedAtUtc);
        }
    }

    /// <summary>Shared by PopulateCustomerRefreshGrid (pre-filling from persisted
    /// history) and ApplyCustomerRefreshOutcomes (filling in a run that just
    /// happened) so both format/color the Applied and Date cells identically. An
    /// INSERT row already reads red end-to-end (see InsertHighlightColumns), so
    /// its own Applied status is conveyed by the text alone, not a second color -
    /// only an UPDATE row gets the green/red success color here.</summary>
    private void SetAppliedCell(DataGridViewRow row, string operation, bool success, DateTimeOffset appliedAtUtc)
    {
        row.Cells["Applied"].Value = success ? "Success" : "Failed";
        row.Cells["Date"].Value = appliedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        if (operation != "INSERT")
        {
            row.Cells["Applied"].Style.ForeColor = success ? Color.FromArgb(28, 138, 87) : InsertRed;
            row.Cells["Applied"].Style.Font = new Font(_customerRefreshGrid.Font, FontStyle.Bold);
        }
    }

    private List<CustomerRefreshCandidate> GetSelectedCustomerRefreshCandidates() =>
        _customerRefreshGrid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells["Select"].Value is true)
            .Select(r => r.Tag as CustomerRefreshCandidate)
            .Where(c => c is not null)
            .Cast<CustomerRefreshCandidate>()
            .ToList();

    /// <summary>Incremental "find and jump to" for the search box - scrolls to and
    /// highlights the first row whose Customer Name contains the typed text
    /// (case-insensitive). Doesn't hide/filter any row, and doesn't touch the
    /// Select checkbox - purely a visual "where is it" aid, kept deliberately
    /// separate from what actually gets processed.</summary>
    private void FindAndSelectCustomerRefreshRow(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return;

        foreach (DataGridViewRow row in _customerRefreshGrid.Rows)
        {
            var name = row.Cells["CustomerName"].Value?.ToString() ?? "";
            if (name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0) continue;

            _customerRefreshGrid.ClearSelection();
            row.Selected = true;
            _customerRefreshGrid.CurrentCell = row.Cells["CustomerName"];
            _customerRefreshGrid.FirstDisplayedScrollingRowIndex = row.Index;
            return;
        }
    }

    /// <summary>Reads the company file path directly from _localSettings/
    /// _appSettings (same fallback order as MainForm.Sage50Tab.cs's own
    /// RefreshSage50Tab) rather than from the Sage 50 tab's _sage50CompanyDataPath
    /// TextBox - this tab's own RefreshAllTabsFromConfig subscription (registered
    /// when this tab is built, BEFORE the Sage 50 tab exists at all, since
    /// Customer Refresh is the 3rd tab and Sage 50 is built later) would otherwise
    /// run before RefreshSage50Tab does on every single config load, reading a
    /// stale/blank value out of that TextBox every time. Reading the settings
    /// source of truth directly sidesteps that subscription-order dependency
    /// entirely.</summary>
    private void RefreshCustomerRefreshTargetLabel()
    {
        var path = _localSettings?.GetString("PortProSage.Sage50.CompanyDataPath")
            ?? _appSettings?.GetString("PortProSage.Sage50.CompanyDataPath");

        _customerRefreshTargetLabel.Text = string.IsNullOrWhiteSpace(path)
            ? "Target Sage 50 company file: (not loaded yet - load the Service config on the Sync tab)"
            : $"Target Sage 50 company file: {path}";
    }

    /// <summary>Called from UpdateManualRunButtonStates (MainForm.RunTab.cs)
    /// whenever the overall service-availability state changes, and locally
    /// whenever row selection changes - Run Selected must reflect BOTH "is
    /// anything else running" and "are any rows actually checked."</summary>
    private void UpdateCustomerRefreshRunButtonEnabled(bool serviceAvailable)
    {
        _customerRefreshServiceAvailable = serviceAvailable;
        UpdateCustomerRefreshRunButtonText();
    }

    private void UpdateCustomerRefreshRunButtonText()
    {
        var count = GetSelectedCustomerRefreshCandidates().Count;
        _customerRefreshRunButton.Text = count > 0 ? $"Run Selected ({count})" : "Run Selected";
        _customerRefreshRunButton.Enabled = _customerRefreshServiceAvailable && count > 0;
    }

    private void StartCustomerRefreshScan()
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
                "Something is already running (automatic or manual) - Customer Refresh can't run at the same " +
                "time, since all of them connect to Sage 50 under the same account.",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Deliberately NOT calling ConfirmProceedIfSage50AppOpen() here (unlike
        // Manual Run/Automatic Service) - confirmed 2026-08-24 that heuristic
        // pre-flight warning isn't wanted on this tab. If Sage 50 really is open
        // under the same username, the run just fails immediately and clearly
        // (Sage50Client.ConnectAsync's own error message already explains why),
        // rather than stopping to ask "continue anyway?" first.
        if (!File.Exists(ServiceExePath))
        {
            MessageBox.Show(this, $"Could not find PortProSage.Service.exe in:\n{_serviceFolderBox.Text}", "Not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var request = new SyncRequest
        {
            FilterType = FilterType.CustomerRefreshScan,
            RequestedBy = Environment.UserName + " (Admin UI - Customer Refresh scan)"
        };

        Directory.CreateDirectory(_manualRunFolder);
        var requestPath = TriggerService.WriteRequest(_manualRunFolder, request);

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
        _pendingRunKind = PendingRunKind.CustomerRefreshScan;
        _resultPollTimer.Start();

        ClearCustomerRefreshGrid();
        _customerRefreshEmptyLabel.Text = "Extracting...";
        _customerRefreshLastScannedLabel.Text = "";

        _manualRunButton.Enabled = false;
        _customerRefreshScanButton.Enabled = false;
        UpdateCustomerRefreshRunButtonEnabled(false);
        _manualRunStopButton.Enabled = true;
        _startServiceButton.Enabled = false;
        _stopServiceButton.Enabled = false;

        RefreshServiceStatus();
        RefreshHistoryList();
    }

    private void StartCustomerRefreshExecute()
    {
        var selected = GetSelectedCustomerRefreshCandidates();
        if (selected.Count == 0)
        {
            MessageBox.Show(this,
                "No rows are selected - tick the checkbox on each customer you want to process first (or use " +
                "Select all).",
                "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var (state, _) = GetServiceRunState();
        if (state != ServiceRunState.NotRunning)
        {
            MessageBox.Show(this,
                "Something is already running (automatic or manual) - Customer Refresh can't run at the same " +
                "time, since all of them connect to Sage 50 under the same account.",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Deliberately NOT calling ConfirmProceedIfSage50AppOpen() here (unlike
        // Manual Run/Automatic Service) - confirmed 2026-08-24 that heuristic
        // pre-flight warning isn't wanted on this tab. If Sage 50 really is open
        // under the same username, the run just fails immediately and clearly
        // (Sage50Client.ConnectAsync's own error message already explains why),
        // rather than stopping to ask "continue anyway?" first.
        if (!File.Exists(ServiceExePath))
        {
            MessageBox.Show(this, $"Could not find PortProSage.Service.exe in:\n{_serviceFolderBox.Text}", "Not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var insertCount = selected.Count(c => c.Operation == "INSERT");
        var updateCount = selected.Count(c => c.Operation == "UPDATE");

        var request = new SyncRequest
        {
            FilterType = FilterType.FullCustomerRefresh,
            RequestedBy = Environment.UserName + " (Admin UI - Customer Refresh)",
            CustomerRefreshDryRun = _customerRefreshDryRun.Checked,
            CustomerRefreshSelectedPortProIds = selected.Select(c => c.PortProCustomerId).ToList()
        };

        // Leads the message (and the dialog title) rather than being buried near
        // the bottom - the write mode is the single most consequential fact in
        // this dialog, and Dry Run now defaults to UNCHECKED (a real write), so
        // whichever mode is actually about to happen needs to be the first thing
        // seen, not something you'd miss by skimming past a "Write mode:" line.
        var modeBanner = _customerRefreshDryRun.Checked
            ? "*** DRY RUN - simulated only, nothing will actually be written to Sage 50. ***\n\n"
            : "*** REAL WRITE - this will make real changes to Sage 50. ***\n\n";
        var dialogTitle = _customerRefreshDryRun.Checked
            ? "DRY RUN - ALERT..!! (Customer Refresh)"
            : "ALERT..!! (Customer Refresh)";

        var confirm = MessageBox.Show(this,
            modeBanner +
            $"Run Customer Refresh for {selected.Count} selected customer(s)?\n\n" +
            $"   {insertCount} will be CREATED (new in Sage 50)\n" +
            $"   {updateCount} will be UPDATED (overwriting existing Sage 50 data)\n\n" +
            "ANY CHANGES MADE DIRECTLY IN SAGE 50 TO AN UPDATED CUSTOMER WILL BE LOST AND REPLACED BY PORTPRO'S " +
            "DATA.\n\n" +
            $"Sage 50 company file: {_sage50CompanyDataPath.Text}\n\n" +
            "Continue?",
            dialogTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        Directory.CreateDirectory(_manualRunFolder);
        var requestPath = TriggerService.WriteRequest(_manualRunFolder, request);

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
        _pendingRunKind = PendingRunKind.CustomerRefreshExecute;
        _resultPollTimer.Start();

        _manualRunButton.Enabled = false;
        _customerRefreshScanButton.Enabled = false;
        UpdateCustomerRefreshRunButtonEnabled(false);
        _manualRunStopButton.Enabled = true;
        _startServiceButton.Enabled = false;
        _stopServiceButton.Enabled = false;

        RefreshServiceStatus();
        SelectHistoryTab();
        RefreshHistoryList();
        SelectTopHistoryRow();
    }

    /// <summary>Same completion pop-up shape as ShowRunCompletionMessage, but
    /// worded for customer counts instead of invoice counts - see FilterType.
    /// FullCustomerRefresh's doc comment for why the same SyncResult fields carry
    /// different meaning here (Created lives in InvoicesSkippedBeforeCutoff,
    /// Updated in InvoicesSkippedAlreadyImported - see Diagnostics.
    /// RunFullCustomerRefreshAsync's mapping). Called from ResultPollTimer_Tick
    /// (MainForm.HistoryTab.cs) instead of ShowRunCompletionMessage whenever the
    /// run just tracked was a Customer Refresh execute.</summary>
    private void ShowCustomerRefreshCompletionMessage(SyncResult? result)
    {
        if (result is null)
        {
            MessageBox.Show(this,
                "The refresh stopped without ever recording a result - it may have crashed immediately. Check the " +
                "Full Log tab (below, in History & Logs) for what happened.",
                "ALERT..!! (Customer Refresh) - did not complete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var dryRunBanner = result.WasDryRun
            ? "*** DRY RUN - simulated only, nothing was actually written to Sage 50. ***\n\n"
            : "";
        var suffix = result.WasDryRun ? " (simulated)" : "";

        if (!result.IsFinal)
        {
            MessageBox.Show(this,
                dryRunBanner +
                "Refresh was INTERRUPTED before finishing - the process stopped unexpectedly (crashed, was force-" +
                "stopped, or hit a fatal Sage 50 write error).\n\n" +
                $"As of its last checkpoint:\n" +
                $"Created{suffix}: {result.InvoicesSkippedBeforeCutoff}\n" +
                $"Updated{suffix}: {result.InvoicesSkippedAlreadyImported}\n" +
                $"Failed: {result.InvoicesFailedImport}\n\n" +
                "Check the Failed Transactions tab or Full Log (below, in History & Logs) for exactly what happened.",
                "ALERT..!! (Customer Refresh) - interrupted", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (result.InvoicesFailedImport > 0)
        {
            MessageBox.Show(this,
                dryRunBanner +
                "Refresh finished WITH ERRORS.\n\n" +
                $"Customers selected: {result.InvoicesFetched}\n" +
                $"Created{suffix}: {result.InvoicesSkippedBeforeCutoff}\n" +
                $"Updated{suffix}: {result.InvoicesSkippedAlreadyImported}\n" +
                $"Failed: {result.InvoicesFailedImport}\n\n" +
                "Check the Failed Transactions tab or Full Log (below, in History & Logs) for exactly what went wrong.",
                "ALERT..!! (Customer Refresh) - finished with errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show(this,
                dryRunBanner +
                "Refresh completed successfully.\n\n" +
                $"Customers selected: {result.InvoicesFetched}\n" +
                $"Created{suffix}: {result.InvoicesSkippedBeforeCutoff}\n" +
                $"Updated{suffix}: {result.InvoicesSkippedAlreadyImported}",
                "ALERT..!! (Customer Refresh) - complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
