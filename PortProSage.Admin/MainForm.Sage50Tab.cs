using System.Text.Json.Nodes;

namespace PortProSage.Admin;

public partial class MainForm
{
    private TextBox _sage50CompanyDataPath = new();
    private TextBox _sage50UserName = new();
    private TextBox _sage50Password = new() { UseSystemPasswordChar = true };
    private TextBox _sage50AppName = new();
    private TextBox _sage50AppId = new();
    private TextBox _sage50ExpectedSdkVersion = new();
    private TextBox _sage50DefaultRevenueAccount = new();
    private TextBox _sage50DefaultReceivableAccount = new();
    private CheckBox _sage50AutoCreateCustomers = new() { Text = "Auto-create missing customers" };
    private CheckBox _sage50AutoCreateItems = new() { Text = "Auto-create missing items/services" };
    private CheckBox _sage50DryRun = new() { Text = "Dry run (simulate writes - no real Sage 50 changes)" };
    private CheckBox _sage50IgnoreAccountMismatchUseDefault = new() { Text = "Ignore account 1-on-1 match and apply default" };
    private TextBox _sage50AccountsUnverifiable = new() { Width = 400 };
    private DataGridView _taxCodesGrid = new() { Width = 400, Height = 120, AllowUserToAddRows = true };
    private DataGridView _chargeAccountMapGrid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = true };
    private SplitContainer _sage50Split = null!;

    // Guards against re-locking a bad SplitterDistance in permanently - see
    // ResizeSage50SplitToFieldsHeight's doc comment.
    private bool _sage50SplitSized;

    private const string ChargeAccountMapHelpText =
        "Controls exactly which Sage 50 GL account each PortPro charge name posts to. Matched by PortPro Charge Name " +
        "(case-insensitive) against each invoice line.\n\n" +
        "Resolution order for every charge: this table's Sage 50 Account Number (if a row exists and it's non-blank) " +
        "> Default revenue account above > error (the whole run stops rather than post to an undefined account).\n\n" +
        "Example: a row with PortPro Charge Name 'PREPULL' and Sage 50 Account Number '4020' sends every PREPULL " +
        "charge to account 4020. Leave Sage 50 Account Number blank on a row to fall back to the Default revenue " +
        "account instead. PortPro glCode/Sage 50 Account Name columns are reference/audit only and don't affect " +
        "what actually gets posted.";

    private TabPage BuildSage50Tab()
    {
        var page = new TabPage("Sage 50");
        var grid = NewFieldGrid();
        const string f = AppSettingsFileName;
        const int fieldPercent = 25;

        AddPercentRow(grid, "App name", _sage50AppName, f, "PortProSage:Sage50:AppName",
            "The friendly application name the Sage 50 SDK asks for when a third-party app registers itself before " +
            "opening a company file. Shows up inside Sage 50 as the name of the connecting application.\n\n" +
            "Example: PortPro Sage 50 Connector",
            fieldPercent);

        var testConnectionButton = new Button { Text = "Test Connection", Width = 110, Height = 23 };
        testConnectionButton.Click += (_, _) => TestSage50Connection();
        AddPercentRow(grid, "Company data path (secret)", _sage50CompanyDataPath, LocalSettingsFileName, "PortProSage:Sage50:CompanyDataPath",
            "The full file path to the Sage 50 company file (.SAI) this integration reads from and writes invoices to.\n\n" +
            "Example: C:\\simplyData\\RS RUSH TRANSFER XPRESS INC-2026.sai\n\n" +
            "This must point at a real, existing company file on this server - the Service opens exactly this file " +
            "every time it connects to Sage 50.\n\n" +
            "\"Test Connection\" attempts a real connect using whatever is currently SAVED to appsettings.Local.json " +
            "- Save Sage 50 settings first if you just changed something, or the test won't reflect your edits.",
            fieldPercent + fieldPercent / 2, testConnectionButton); // 50% wider than the other fields on this tab
        AddPercentRow(grid, "Sage50 User Name (secret)", _sage50UserName, LocalSettingsFileName, "PortProSage:Sage50:UserName",
            "The Sage 50 login the Service uses to open the company file. Must be a dedicated account, never the " +
            "same one a human logs into Sage 50 with interactively - Sage 50 rejects two simultaneous sessions " +
            "under the same username, even in multi-user mode.\n\nExample: PortProConnect",
            fieldPercent);
        AddPercentRow(grid, "Sage50 Password (secret)", _sage50Password, LocalSettingsFileName, "PortProSage:Sage50:Password",
            "The password for the Sage 50 username above.",
            fieldPercent);
        AddPercentRow(grid, "App ID (max 6 chars)", _sage50AppId, f, "PortProSage:Sage50:AppId",
            "A short code (max 6 characters) identifying this application to the Sage 50 SDK - required alongside " +
            "App name to register before opening a company file.\n\nExample: PPS50",
            fieldPercent);
        AddPercentRow(grid, "Expected SDK version", _sage50ExpectedSdkVersion, f, "PortProSage:Sage50:ExpectedSdkVersion",
            "If set, the Service logs a warning at startup when the bundled Sage 50 SDK's version doesn't start with " +
            "this text - a sanity check that the SDK files match what's actually installed on this server. Leave " +
            "blank to skip the check entirely.\n\nExample: 2026.2",
            fieldPercent);
        AddPercentRow(grid, "Default revenue account", _sage50DefaultRevenueAccount, f, "PortProSage:Sage50:DefaultRevenueAccount",
            "The GL account a PortPro charge posts to when it has no specific row in the Charge account map below, " +
            "or has a row with a blank Sage 50 Account Number. If this is also blank, that invoice fails with an " +
            "error instead of posting to an undefined account.\n\n" +
            "Example: 4100  ->  a 'PICK UP & DELIVERY' charge with no map entry posts to account 4100.",
            fieldPercent);
        AddCheckRow(grid, _sage50IgnoreAccountMismatchUseDefault, f, "PortProSage:Sage50:IgnoreAccountMismatchUseDefault",
            "Checked: if a charge's specific account (from the Charge account map below) can't be confirmed as a " +
            "real Sage 50 account, the run does NOT stop - it just posts to the Default revenue account above " +
            "instead, and makes a note of it (not an error, just a warning you can review later).\n\n" +
            "Unchecked (the normal setting): a charge's mapped account must match a real Sage 50 account exactly. " +
            "If it doesn't, that invoice fails right there and the run stops on it.\n\n" +
            "Either way, if the Default revenue account itself can't be confirmed, that always stops the run - " +
            "there's nothing left to fall back to at that point.\n\n" +
            "Turn this on if you keep seeing an account rejected that you've personally checked and confirmed is " +
            "really there in Sage 50 - rather than tracking down each one individually, this just tells the sync " +
            "\"when in doubt, use my default account instead of stopping everything.\"");
        AddPercentRow(grid, "Default receivable account", _sage50DefaultReceivableAccount, f, "PortProSage:Sage50:DefaultReceivableAccount",
            "The GL accounts-receivable account assigned to a customer that gets auto-created because they didn't " +
            "already exist in Sage 50.\n\nExample: 1200",
            fieldPercent);
        AddPercentRow(grid, "Accounts To Trust\n(comma-separated)", _sage50AccountsUnverifiable, f, "PortProSage:Sage50:AccountsUnverifiableBySdk",
            "USE THIS ONLY WHEN: a sync run fails with an error saying a GL account \"does not exist\" in Sage 50 " +
            "- but when you open Sage 50 yourself and check the Chart of Accounts, that account IS actually there " +
            "and set up correctly.\n\n" +
            "This is a known quirk with a handful of Sage 50 accounts - the connecting software sometimes wrongly " +
            "reports them as missing even though they're real. Listing an account number here tells the sync to " +
            "trust it and skip that faulty check.\n\n" +
            "STEPS if you hit this:\n" +
            "1. Open the History & Logs tab, find the failed run, and read the exact account number in the error " +
            "(e.g. \"Revenue account 4100 ... does not exist\").\n" +
            "2. Open Sage 50 and confirm that account number is really there.\n" +
            "3. If it IS there: add that number to this box (comma-separated) and save - the error should stop.\n" +
            "4. If it is NOT there: do NOT add it here. That's a real setup problem - fix the account in Sage 50, " +
            "or correct the \"Default revenue account\"/\"Default receivable account\"/Charge account map fields " +
            "above instead.\n\n" +
            "Current entries: 4100 and 4110 - this company's Canadian and US-dollar revenue accounts, confirmed " +
            "real in Sage 50 in August 2026 but wrongly flagged as missing.",
            fieldPercent);

        AddCheckRow(grid, _sage50AutoCreateCustomers, f, "PortProSage:Sage50:AutoCreateCustomers",
            "When checked, a PortPro customer that doesn't already exist in Sage 50 is created automatically before " +
            "posting their invoice. When unchecked, that invoice fails validation instead (\"customer not found\") " +
            "rather than silently creating new customer records.");
        AddCheckRow(grid, _sage50AutoCreateItems, f, "PortProSage:Sage50:AutoCreateItems",
            "Same idea as auto-creating customers, but for service items/charges. When checked, a PortPro charge " +
            "name with no matching Sage 50 item (e.g. 'FUEL SURCHARGE 3') gets a new item created automatically, " +
            "using whatever account the Charge account map / Default revenue account resolves to.");
        AddCheckRow(grid, _sage50DryRun, f, "PortProSage:Sage50:DryRun",
            "The most important switch on this whole screen. Checked = simulated: the Service logs exactly what it " +
            "would create or post, but writes nothing at all to Sage 50. Unchecked = real: invoices, customers, and " +
            "items are actually created in Sage 50 for real. Always test a change with this checked first.");

        SetupTaxCodesGrid();
        AddPercentRow(grid, "Tax codes\n(PortPro abbreviation -> Sage 50 code)", _taxCodesGrid, f, "PortProSage:Sage50:TaxCodesByAbbreviation",
            "Maps a Canadian tax abbreviation PortPro shows in a charge name (HST/GST/PST/QST) to the matching tax " +
            "code string from Sage 50's own Setup > Settings > Company > Sales Taxes > Tax Codes screen. A charge " +
            "recognized this way is NOT imported as its own line item - instead Sage 50 calculates and applies that " +
            "tax code directly to the invoice's real revenue lines.\n\n" +
            "Example: Abbreviation 'HST' -> Sage 50 code 'H' (this company's code for HST 13%, posting to account 2310).",
            30);

        SetupChargeAccountMapGrid();
        WireSource(_chargeAccountMapGrid, f, "PortProSage:Sage50:ChargeAccountMap");
        var mapLabelText = new Label
        {
            Text = "Charge account map - blank Sage50AccountNumber falls back to Default revenue account above:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(4, 4, 0, 4)
        };
        var mapLabelHelp = CreateHelpIcon("Charge account map", ChargeAccountMapHelpText);
        mapLabelHelp.Anchor = AnchorStyles.Left;
        var mapLabel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 28,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        mapLabel.Controls.Add(mapLabelText);
        mapLabel.Controls.Add(mapLabelHelp);

        var save = new Button { Text = "Save Sage 50 settings", Dock = DockStyle.Bottom, Height = 32 };
        save.Click += (_, _) => SaveSage50Tab();

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        var mapPanel = new Panel { Dock = DockStyle.Fill };
        mapPanel.Controls.Add(_chargeAccountMapGrid);
        mapPanel.Controls.Add(mapLabel);

        // User-resizable split, sized to the fields' own real (now-compact)
        // height rather than a guessed constant - PreferredSize reflects the
        // actual sum of the AddPercentRow/AddCheckRow rows above. Whatever this
        // doesn't need goes to Panel2 (the charge account map grid)
        // automatically, since SplitContainer always sizes Panel2 to
        // "whatever's left".
        _sage50Split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        _sage50Split.Panel1.Controls.Add(fieldsScroll);
        _sage50Split.Panel2.Controls.Add(mapPanel);
        ResizeSage50SplitToFieldsHeight(grid);
        // This tab isn't necessarily the one selected when the form first
        // loads, so the attempt above may have run before _sage50Split had its
        // real, final size - retry once it's actually shown, matching the
        // History & Logs tab's same "may not be visible yet" fix.
        page.Enter += (_, _) => ResizeSage50SplitToFieldsHeight(grid);

        page.Controls.Add(_sage50Split);
        page.Controls.Add(save);

        RefreshAllTabsFromConfig += RefreshSage50Tab;
        return page;
    }

    /// <summary>Sets _sage50Split's SplitterDistance to the fields grid's real
    /// PreferredSize height - guarded so a bad attempt (this tab not yet shown,
    /// so PreferredSize/the split's own Height aren't reliable yet) doesn't
    /// lock in a wrong value: only marks itself done once the computed height
    /// looks sane (grid.PreferredSize.Height > 0, i.e. rows have actually been
    /// measured) AND the assignment itself succeeds (SplitContainer validates
    /// SplitterDistance against its own current Height, which can itself still
    /// be unrealized at the very first attempt).</summary>
    private void ResizeSage50SplitToFieldsHeight(TableLayoutPanel grid)
    {
        if (_sage50SplitSized || grid.PreferredSize.Height <= 0) return;

        try
        {
            _sage50Split.SplitterDistance = Math.Max(200, grid.PreferredSize.Height + 16);
            _sage50SplitSized = true;
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    /// <summary>Places `content` at a fixed PERCENTAGE of the row's available width -
    /// a nested TableLayoutPanel Percent column, not a fixed pixel value, so it
    /// stays exactly that fraction of the panel as the window resizes. Requested
    /// specifically for this tab: the other tabs' fixed-pixel FieldHalfWidth
    /// approach looked fine there, but far too wide here on a large/wide window.
    /// Any trailing controls (a button, the help icon) sit immediately after
    /// `content`, inside the SAME percentage share at their own natural width -
    /// only `content` itself stretches to fill what's left within that share, and
    /// the other (100-percent)% of the row is left empty, not consumed by
    /// anything.</summary>
    private void AddPercentRow(TableLayoutPanel grid, string labelText, Control content, string fileName, string jsonPath,
        string helpText, int percent, params Control[] trailingControls)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 8, 3, 3) };
        grid.Controls.Add(label, 0, row);

        var trailing = trailingControls.ToList();
        if (!string.IsNullOrEmpty(helpText))
        {
            trailing.Add(CreateHelpIcon(labelText.Replace("\n", " "), helpText));
        }

        // Fixed width (computed below), not Anchor=Left|Right stretch - the
        // width itself IS the "percent of the row" behavior here.
        content.Anchor = AnchorStyles.Left;
        content.Margin = new Padding(3, 4, 3, 4);

        // FlowLayoutPanel, not a nested Dock=Fill TableLayoutPanel (the
        // previous implementation) - a TableLayoutPanel row using AutoSize
        // does not reliably compute a correct height, or even render its
        // content at all once a height is forced on it, when the row's own
        // cell content is itself a Dock=Fill nested TableLayoutPanel;
        // confirmed live twice (first a large empty-looking gap between
        // fields, then collapsed/invisible field content once the row height
        // was forced explicitly to close that gap). FlowLayoutPanel's own
        // AutoSize computation doesn't have that ambiguity - it's the exact
        // mechanism AddRow/AddCheckRow already use successfully everywhere
        // else in this app.
        var wrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };
        wrap.Controls.Add(content);
        foreach (var t in trailing)
        {
            t.Anchor = AnchorStyles.Left;
            t.Margin = new Padding(4, 4, 0, 3);
            wrap.Controls.Add(t);
        }
        grid.Controls.Add(wrap, 1, row);

        // content's own pixel WIDTH is set to `percent`% of the grid's column-1
        // width by hand, refreshed whenever the grid resizes - not via nested
        // TableLayoutPanel percent columns (see why above). Column 1's width is
        // whatever's left of the grid after its own fixed 220px label column,
        // 34px (unused here - the help icon lives inside `wrap` instead, not
        // grid's own column 2) and padding. grid.ClientSize isn't reliably real
        // yet the first time this runs (this tab may not be the one selected
        // when the form first loads), so this is refreshed again once it is -
        // see BuildSage50Tab's page.Enter hook.
        void ApplyWidth()
        {
            var column1Width = Math.Max(150, grid.ClientSize.Width - grid.Padding.Horizontal - 220 - 34);
            var trailingWidth = trailing.Sum(t => t.Width + t.Margin.Horizontal);
            content.Width = Math.Max(80, (int)(column1Width * percent / 100.0) - trailingWidth);
        }

        grid.SizeChanged += (_, _) => ApplyWidth();
        ApplyWidth();

        WireSource(content, fileName, jsonPath);
    }

    private void AddCheckRow(TableLayoutPanel grid, CheckBox box, string fileName, string jsonPath, string helpText = "")
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        box.AutoSize = true;
        box.Margin = new Padding(3, 8, 3, 3);
        // A checkbox never fills the column (AutoSize sizes it to its own Text) -
        // wrap it with the help icon so the icon sits right after the label text
        // instead of stranded at column 2's fixed far-right position.
        AddWrappedWithHelp(grid, row, box, box.Text, helpText);
        WireSource(box, fileName, jsonPath);
    }

    private void SetupTaxCodesGrid()
    {
        _taxCodesGrid.Columns.Add("Abbreviation", "PortPro Abbreviation");
        _taxCodesGrid.Columns.Add("Sage50Code", "Sage 50 Tax Code");
        _taxCodesGrid.RowHeadersVisible = false;

        // Proportional widths (FillWeight, not pixels) - same pattern as the
        // History tab's grids: sums to 100, so these read directly as percentages.
        _taxCodesGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _taxCodesGrid.Columns["Abbreviation"].FillWeight = 50;
        _taxCodesGrid.Columns["Sage50Code"].FillWeight = 50;
    }

    private void SetupChargeAccountMapGrid()
    {
        _chargeAccountMapGrid.Columns.Add("PortProChargeName", "PortPro Charge Name");
        _chargeAccountMapGrid.Columns.Add("PortProChargeNumber", "PortPro glCode (reference only)");
        _chargeAccountMapGrid.Columns.Add("Sage50AccountName", "Sage 50 Account Name (reference only)");
        _chargeAccountMapGrid.Columns.Add("Sage50AccountNumber", "Sage 50 Account Number (used)");
        _chargeAccountMapGrid.RowHeadersVisible = false;

        // Proportional widths (FillWeight, not pixels) - the two reference-only
        // columns get less room than the charge name (what's matched on) and the
        // account number (what actually gets used).
        _chargeAccountMapGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _chargeAccountMapGrid.Columns["PortProChargeName"].FillWeight = 30;
        _chargeAccountMapGrid.Columns["PortProChargeNumber"].FillWeight = 15;
        _chargeAccountMapGrid.Columns["Sage50AccountName"].FillWeight = 25;
        _chargeAccountMapGrid.Columns["Sage50AccountNumber"].FillWeight = 30;
    }

    private void RefreshSage50Tab()
    {
        if (_appSettings is null) return;

        _sage50CompanyDataPath.Text = _localSettings?.GetString("PortProSage.Sage50.CompanyDataPath")
            ?? _appSettings.GetString("PortProSage.Sage50.CompanyDataPath");
        _sage50UserName.Text = _localSettings?.GetString("PortProSage.Sage50.UserName")
            ?? _appSettings.GetString("PortProSage.Sage50.UserName");
        _sage50Password.Text = _localSettings?.GetString("PortProSage.Sage50.Password") ?? "";
        _sage50AppName.Text = _appSettings.GetString("PortProSage.Sage50.AppName");
        _sage50AppId.Text = _appSettings.GetString("PortProSage.Sage50.AppId");
        _sage50ExpectedSdkVersion.Text = _appSettings.GetString("PortProSage.Sage50.ExpectedSdkVersion");
        _sage50DefaultRevenueAccount.Text = _appSettings.GetString("PortProSage.Sage50.DefaultRevenueAccount");
        _sage50DefaultReceivableAccount.Text = _appSettings.GetString("PortProSage.Sage50.DefaultReceivableAccount");
        _sage50AutoCreateCustomers.Checked = _appSettings.GetBool("PortProSage.Sage50.AutoCreateCustomers");
        _sage50AutoCreateItems.Checked = _appSettings.GetBool("PortProSage.Sage50.AutoCreateItems");
        _sage50DryRun.Checked = _appSettings.GetBool("PortProSage.Sage50.DryRun");
        _sage50IgnoreAccountMismatchUseDefault.Checked = _appSettings.GetBool("PortProSage.Sage50.IgnoreAccountMismatchUseDefault");
        _sage50AccountsUnverifiable.Text = string.Join(", ", _appSettings.GetStringArray("PortProSage.Sage50.AccountsUnverifiableBySdk"));

        _taxCodesGrid.Rows.Clear();
        foreach (var kvp in _appSettings.GetStringDictionary("PortProSage.Sage50.TaxCodesByAbbreviation"))
        {
            _taxCodesGrid.Rows.Add(kvp.Key, kvp.Value);
        }

        _chargeAccountMapGrid.Rows.Clear();
        foreach (var item in _appSettings.GetArray("PortProSage.Sage50.ChargeAccountMap"))
        {
            if (item is not JsonObject obj) continue;
            _chargeAccountMapGrid.Rows.Add(
                obj["PortProChargeName"]?.GetValue<string>() ?? "",
                obj["PortProChargeNumber"]?.GetValue<string>() ?? "",
                obj["Sage50AccountName"]?.GetValue<string>() ?? "",
                obj["Sage50AccountNumber"]?.GetValue<string>() ?? "");
        }
    }

    private void SaveSage50Tab()
    {
        if (_appSettings is null || _localSettings is null) return;

        _appSettings.SetString("PortProSage.Sage50.AppName", _sage50AppName.Text);
        _appSettings.SetString("PortProSage.Sage50.AppId", _sage50AppId.Text);
        _appSettings.SetString("PortProSage.Sage50.ExpectedSdkVersion", _sage50ExpectedSdkVersion.Text);
        _appSettings.SetString("PortProSage.Sage50.DefaultRevenueAccount", _sage50DefaultRevenueAccount.Text);
        _appSettings.SetString("PortProSage.Sage50.DefaultReceivableAccount", _sage50DefaultReceivableAccount.Text);
        _appSettings.SetBool("PortProSage.Sage50.AutoCreateCustomers", _sage50AutoCreateCustomers.Checked);
        _appSettings.SetBool("PortProSage.Sage50.AutoCreateItems", _sage50AutoCreateItems.Checked);
        _appSettings.SetBool("PortProSage.Sage50.DryRun", _sage50DryRun.Checked);
        _appSettings.SetBool("PortProSage.Sage50.IgnoreAccountMismatchUseDefault", _sage50IgnoreAccountMismatchUseDefault.Checked);
        _appSettings.SetStringArray("PortProSage.Sage50.AccountsUnverifiableBySdk",
            _sage50AccountsUnverifiable.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var taxCodes = _taxCodesGrid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && r.Cells["Abbreviation"].Value is not null)
            .Select(r => new KeyValuePair<string, string>(
                r.Cells["Abbreviation"].Value?.ToString() ?? "",
                r.Cells["Sage50Code"].Value?.ToString() ?? ""));
        _appSettings.SetStringDictionary("PortProSage.Sage50.TaxCodesByAbbreviation", taxCodes);

        var map = new JsonArray();
        foreach (DataGridViewRow r in _chargeAccountMapGrid.Rows)
        {
            if (r.IsNewRow) continue;
            var name = r.Cells["PortProChargeName"].Value?.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            map.Add(new JsonObject
            {
                ["PortProChargeName"] = name,
                ["PortProChargeNumber"] = r.Cells["PortProChargeNumber"].Value?.ToString() ?? "",
                ["Sage50AccountName"] = r.Cells["Sage50AccountName"].Value?.ToString() ?? "",
                ["Sage50AccountNumber"] = r.Cells["Sage50AccountNumber"].Value?.ToString() ?? ""
            });
        }
        _appSettings.SetArray("PortProSage.Sage50.ChargeAccountMap", map);
        _appSettings.Save();

        _localSettings.SetString("PortProSage.Sage50.CompanyDataPath", _sage50CompanyDataPath.Text);
        _localSettings.SetString("PortProSage.Sage50.UserName", _sage50UserName.Text);
        _localSettings.SetString("PortProSage.Sage50.Password", _sage50Password.Text);
        _localSettings.Save();

        MessageBox.Show(this, "Sage 50 settings saved. The running Service needs a restart to pick up changes.", "Saved",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
