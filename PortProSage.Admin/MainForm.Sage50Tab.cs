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
    private TextBox _sage50AccountsUnverifiable = new() { Width = 400 };
    private DataGridView _taxCodesGrid = new() { Width = 400, Height = 120, AllowUserToAddRows = true };
    private DataGridView _chargeAccountMapGrid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = true };

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
        AddRow(grid, "App name", _sage50AppName, f, "PortProSage:Sage50:AppName",
            "The friendly application name the Sage 50 SDK asks for when a third-party app registers itself before " +
            "opening a company file. Shows up inside Sage 50 as the name of the connecting application.\n\n" +
            "Example: PortPro Sage 50 Connector");
        AddRowWithButton(grid, "Company data path (secret)", _sage50CompanyDataPath, LocalSettingsFileName, "PortProSage:Sage50:CompanyDataPath",
            "The full file path to the Sage 50 company file (.SAI) this integration reads from and writes invoices to.\n\n" +
            "Example: C:\\simplyData\\RS RUSH TRANSFER XPRESS INC-2026.sai\n\n" +
            "This must point at a real, existing company file on this server - the Service opens exactly this file " +
            "every time it connects to Sage 50.\n\n" +
            "\"Test Connection\" attempts a real connect using whatever is currently SAVED to appsettings.Local.json " +
            "- Save Sage 50 settings first if you just changed something, or the test won't reflect your edits.",
            "Test Connection", (_, _) => TestSage50Connection());
        AddRow(grid, "Sage50 User Name (secret)", _sage50UserName, LocalSettingsFileName, "PortProSage:Sage50:UserName",
            "The Sage 50 login the Service uses to open the company file. Must be a dedicated account, never the " +
            "same one a human logs into Sage 50 with interactively - Sage 50 rejects two simultaneous sessions " +
            "under the same username, even in multi-user mode.\n\nExample: PortProConnect");
        AddRow(grid, "Sage50 Password (secret)", _sage50Password, LocalSettingsFileName, "PortProSage:Sage50:Password",
            "The password for the Sage 50 username above.");
        AddRow(grid, "App ID (max 6 chars)", _sage50AppId, f, "PortProSage:Sage50:AppId",
            "A short code (max 6 characters) identifying this application to the Sage 50 SDK - required alongside " +
            "App name to register before opening a company file.\n\nExample: PPS50");
        AddRow(grid, "Expected SDK version", _sage50ExpectedSdkVersion, f, "PortProSage:Sage50:ExpectedSdkVersion",
            "If set, the Service logs a warning at startup when the bundled Sage 50 SDK's version doesn't start with " +
            "this text - a sanity check that the SDK files match what's actually installed on this server. Leave " +
            "blank to skip the check entirely.\n\nExample: 2026.2");
        AddRow(grid, "Default revenue account", _sage50DefaultRevenueAccount, f, "PortProSage:Sage50:DefaultRevenueAccount",
            "The GL account a PortPro charge posts to when it has no specific row in the Charge account map below, " +
            "or has a row with a blank Sage 50 Account Number. If this is also blank, that invoice fails with an " +
            "error instead of posting to an undefined account.\n\n" +
            "Example: 4100  ->  a 'PICK UP & DELIVERY' charge with no map entry posts to account 4100.");
        AddRow(grid, "Default receivable account", _sage50DefaultReceivableAccount, f, "PortProSage:Sage50:DefaultReceivableAccount",
            "The GL accounts-receivable account assigned to a customer that gets auto-created because they didn't " +
            "already exist in Sage 50.\n\nExample: 1200");
        AddRow(grid, "Accounts unverifiable by SDK\n(comma-separated)", _sage50AccountsUnverifiable, f, "PortProSage:Sage50:AccountsUnverifiableBySdk",
            "A workaround list, not a mapping. Some Sage 50 accounts (confirmed for this company: currency-paired " +
            "accounts like 4100/4110) make the SDK's own 'does this account exist?' check incorrectly return false, " +
            "even though the account is real and valid. Any account number listed here skips that broken check and " +
            "is trusted to exist - only add an account here after confirming directly in Sage 50 that it's real.\n\n" +
            "Example: 4100, 4110");

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
        AddRow(grid, "Tax codes\n(PortPro abbreviation -> Sage 50 code)", _taxCodesGrid, f, "PortProSage:Sage50:TaxCodesByAbbreviation",
            "Maps a Canadian tax abbreviation PortPro shows in a charge name (HST/GST/PST/QST) to the matching tax " +
            "code string from Sage 50's own Setup > Settings > Company > Sales Taxes > Tax Codes screen. A charge " +
            "recognized this way is NOT imported as its own line item - instead Sage 50 calculates and applies that " +
            "tax code directly to the invoice's real revenue lines.\n\n" +
            "Example: Abbreviation 'HST' -> Sage 50 code 'H' (this company's code for HST 13%, posting to account 2310).");

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

        // User-resizable split, not a fixed guessed height - both the field
        // list and the charge map grid can need more room depending on how
        // many charges are mapped.
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 340 };
        split.Panel1.Controls.Add(fieldsScroll);
        split.Panel2.Controls.Add(mapPanel);

        page.Controls.Add(split);
        page.Controls.Add(save);

        RefreshAllTabsFromConfig += RefreshSage50Tab;
        return page;
    }

    private void AddCheckRow(TableLayoutPanel grid, CheckBox box, string fileName, string jsonPath, string helpText = "")
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        box.AutoSize = true;
        box.Margin = new Padding(3, 8, 3, 3);
        grid.Controls.Add(box, 1, row);
        if (!string.IsNullOrEmpty(helpText))
        {
            grid.Controls.Add(CreateHelpIcon(box.Text, helpText), 2, row);
        }
        WireSource(box, fileName, jsonPath);
    }

    private void SetupTaxCodesGrid()
    {
        _taxCodesGrid.Columns.Add("Abbreviation", "PortPro Abbreviation");
        _taxCodesGrid.Columns.Add("Sage50Code", "Sage 50 Tax Code");
        _taxCodesGrid.RowHeadersVisible = false;
    }

    private void SetupChargeAccountMapGrid()
    {
        _chargeAccountMapGrid.Columns.Add("PortProChargeName", "PortPro Charge Name");
        _chargeAccountMapGrid.Columns.Add("PortProChargeNumber", "PortPro glCode (reference only)");
        _chargeAccountMapGrid.Columns.Add("Sage50AccountName", "Sage 50 Account Name (reference only)");
        _chargeAccountMapGrid.Columns.Add("Sage50AccountNumber", "Sage 50 Account Number (used)");
        _chargeAccountMapGrid.RowHeadersVisible = false;
        foreach (DataGridViewColumn col in _chargeAccountMapGrid.Columns) col.Width = 190;
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
