using System.Text.Json.Nodes;
using PortProSage.Admin.Services;

namespace PortProSage.Admin;

public partial class MainForm
{
    // Editable combo, not a plain TextBox - confirmed 2026-08-24 the operator
    // wants both: pick a previously-used path from the dropdown (see
    // RefreshSage50CompanyDataPathDropdown/RecordSage50Path), or type/paste a new
    // one directly. DropDownStyle.DropDown (not DropDownList) is what keeps free
    // typing possible - DropDownList would force picking only from the list.
    private ComboBox _sage50CompanyDataPath = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private TextBox _sage50UserName = new();
    private TextBox _sage50Password = new() { UseSystemPasswordChar = true };
    private TextBox _sage50AppName = new();
    private TextBox _sage50AppId = new();
    private TextBox _sage50ExpectedSdkVersion = new();
    private TextBox _sage50DefaultRevenueAccount = new();
    private TextBox _sage50DefaultReceivableAccount = new();
    private NumericUpDown _sage50DefaultNetTermDays = new() { Minimum = 0, Maximum = 365 };
    private CheckBox _sage50AutoCreateCustomers = new() { Text = "Auto-create missing customers" };
    private CheckBox _sage50SyncCustomerUpdates = new() { Text = "Update Customer with latest changes in PortPro" };
    private CheckBox _sage50AutoCreateItems = new() { Text = "Auto-create missing items/services" };
    private CheckBox _sage50DryRun = new() { Text = "Dry run (Simulated - Default 10 Invoices and no real Sage 50 Changes)" };
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

        var browseCompanyDataPathButton = new Button { Text = "Browse...", Width = 80, Height = 23 };
        browseCompanyDataPathButton.Click += (_, _) => BrowseForSage50CompanyDataPath();
        var testConnectionButton = new Button { Text = "Test Connection", Width = 110, Height = 23 };
        testConnectionButton.Click += (_, _) => TestSage50Connection();
        AddPercentRow(grid, "Company data path (secret)", _sage50CompanyDataPath, LocalSettingsFileName, "PortProSage:Sage50:CompanyDataPath",
            "The full file path to the Sage 50 company file (.SAI) this integration reads from and writes invoices to.\n\n" +
            "Example: C:\\simplyData\\RS RUSH TRANSFER XPRESS INC-2026.sai\n\n" +
            "This must point at a real, existing company file on this server - the Service opens exactly this file " +
            "every time it connects to Sage 50.\n\n" +
            "Type or paste a path directly, pick one you've used before from the dropdown, or click Browse... to " +
            "find the .SAI file on disk.\n\n" +
            "Picking a previously-used path from the dropdown also restores the rest of this tab (username, " +
            "password, account defaults, tax codes, charge account map) to whatever was last saved for that " +
            "specific path - since a different company file can genuinely need a different Sage 50 login or " +
            "chart-of-accounts mapping. Typing a brand-new path leaves everything else as-is.\n\n" +
            "\"Test Connection\" saves this tab automatically first, then attempts a real connect using exactly " +
            "what's currently in these fields - no need to click Save Sage 50 settings separately beforehand.",
            fieldPercent + fieldPercent / 2, browseCompanyDataPathButton, testConnectionButton); // 50% wider than the other fields on this tab
        // Picking (not typing) a path from the dropdown restores that path's own
        // saved configuration - see OnSage50PathSelected/Sage50ConfigSnapshotService.
        // Named method, not a lambda - RefreshSage50CompanyDataPathDropdown needs
        // to unsubscribe/resubscribe this exact handler around Items.Clear().
        _sage50CompanyDataPath.SelectedIndexChanged += OnSage50CompanyDataPathSelectedIndexChanged;
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
            "Controls what happens when a charge's resolved account (from the Charge account map below, or the " +
            "Default revenue account if the charge has no map entry) can't be confirmed to exist in Sage 50.\n\n" +
            "1) Unchecked (the normal setting): the resolved account must be confirmed as a real Sage 50 account. " +
            "If it can't be, that invoice fails right there and the run stops on it - nothing gets posted.\n" +
            "Example: charge 'STORAGE' is mapped to account 4020 below, but 4020 doesn't exist in this company's " +
            "chart of accounts -> the invoice fails with an error naming account 4020.\n\n" +
            "2) Checked: same check, but on failure the run does NOT stop - it falls back once to the Default " +
            "revenue account above and posts there instead, leaving a warning to review later.\n" +
            "Example: same 'STORAGE' -> 4020 mapping, box checked, Default revenue account is 4100 -> the invoice " +
            "posts to 4100 instead of failing, with a warning: \"Charge 'STORAGE' was mapped to account '4020', " +
            "which could not be confirmed in Sage 50 - used the Default revenue account '4100' instead.\"\n\n" +
            "Either way, the Charge account map below is the hard, authoritative source for which account a charge " +
            "is supposed to use - this checkbox never changes that mapping, it only decides what happens at posting " +
            "time if Sage 50 can't confirm the mapped (or default) account still exists. And either way, if the " +
            "Default revenue account itself can't be confirmed, that always stops the run - there's nothing left " +
            "to fall back to at that point.\n\n" +
            "Checked effectively runs the sync with a safety net: every charge is guaranteed to post somewhere " +
            "(its own mapped account if valid, otherwise the Default revenue account) rather than blocking on a " +
            "bad or stale account mapping.");
        AddPercentRow(grid, "Default receivable account", _sage50DefaultReceivableAccount, f, "PortProSage:Sage50:DefaultReceivableAccount",
            "Confirmed live 2026-08-21 (checked directly against the Sage 50 SDK): this currently has NO EFFECT - " +
            "Sage 50's customer object has no per-customer receivable-account property to write it to. Simply " +
            "Accounting/Sage 50 posts every customer to one global AR control account, configured once in Sage 50 " +
            "itself (Setup > Settings > Customers & Sales > Linked Accounts), not per customer through this " +
            "integration. Left here in case a future Sage 50 SDK version adds support for it.",
            fieldPercent);
        AddPercentRow(grid, "Default net payment terms (days)", _sage50DefaultNetTermDays, f, "PortProSage:Sage50:DefaultNetTermDays",
            "How many days after the invoice date it's due (Sage 50's \"Net 30\"-style term) - this is what actually " +
            "fixes Sage 50 showing \"Due Date = Invoice Date\" on every imported invoice, a real bug confirmed live " +
            "2026-08-21 (Sage 50 was never being told the terms at all, so it defaulted to Net 0).\n\n" +
            "PortPro sends its own real payment terms on every invoice, and this app uses THAT first - this field " +
            "is only the fallback for the rare case PortPro's value is missing or in a unit other than days.\n\n" +
            "Example: 30",
            10);
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
        AddCheckRow(grid, _sage50SyncCustomerUpdates, f, "PortProSage:Sage50:SyncCustomerUpdatesFromPortPro",
            "Checked (default): once per Automatic Service cycle and once per Manual Run, this checks every " +
            "PortPro customer for a profile change (address/email/contact/phone/currency/terms) since it was last " +
            "synced, and pushes any change into the matching EXISTING Sage 50 customer automatically.\n\n" +
            "This does NOT affect creating brand new customers - that always happens (with the full PortPro " +
            "profile) regardless of this setting, whenever an invoice needs a customer Sage 50 doesn't have yet.\n\n" +
            "⚠ Two real risks to weigh before leaving this checked:\n" +
            "1. PortPro always wins. If someone corrects a customer's address/email/etc. directly in Sage 50, " +
            "that correction is silently overwritten the next time PortPro's own record for that customer changes.\n" +
            "2. Blast radius. Any Sage 50 write failure in this app terminates the WHOLE Service process " +
            "immediately (a deliberate safety policy after a past incident, not specific to this feature) - so a " +
            "single problematic customer record (renamed, deleted, etc. in Sage 50) could crash an otherwise-" +
            "healthy sync cycle, not just skip that one customer.\n\n" +
            "Unchecked: existing Sage 50 customers are left alone forever once created - only brand new customers " +
            "are ever written.");
        AddCheckRow(grid, _sage50AutoCreateItems, f, "PortProSage:Sage50:AutoCreateItems",
            "Same idea as auto-creating customers, but for service items/charges. When checked, a PortPro charge " +
            "name with no matching Sage 50 item (e.g. 'FUEL SURCHARGE 3') gets a new item created automatically, " +
            "using whatever account the Charge account map / Default revenue account resolves to.");
        AddCheckRow(grid, _sage50DryRun, f, "PortProSage:Sage50:DryRun",
            "The most important switch on this whole screen. Checked = simulated: the Service logs exactly what it " +
            "would create or post, but writes nothing at all to Sage 50. Unchecked = real: invoices, customers, and " +
            "items are actually created in Sage 50 for real. Always test a change with this checked first.\n\n" +
            "Also editable directly from the Manual Run tab (the exact same setting, shown in both places) - " +
            "toggling it either place saves immediately and takes effect everywhere.");
        WireDryRunControl(_sage50DryRun);

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

        var save = new Button { Text = "Save Sage 50 settings" };
        save.Click += (_, _) => SaveSage50Tab();
        var saveBar = CreateActionButtonBar(save);

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
        page.Controls.Add(saveBar);

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

    /// <summary>Opens a real file picker for the .SAI company file, defaulting to
    /// whatever folder the currently-typed path (if any) already points at - so
    /// re-browsing after already having a value starts somewhere sensible instead
    /// of always landing back at a default OS folder.</summary>
    private void BrowseForSage50CompanyDataPath()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select Sage 50 Company File",
            Filter = "Sage 50 Company Files (*.SAI)|*.SAI|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true
        };

        var current = _sage50CompanyDataPath.Text;
        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                var dir = Path.GetDirectoryName(current);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) dialog.InitialDirectory = dir;
            }
            catch (ArgumentException)
            {
                // current wasn't a well-formed path (e.g. mid-edit) - just fall back
                // to the OS default starting folder rather than failing the browse.
            }
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _sage50CompanyDataPath.Text = dialog.FileName;
        }
    }

    /// <summary>Populates the Company data path dropdown by UNIONING two
    /// different sources - confirmed live 2026-08-24 relying on just one of them
    /// left a genuinely-used path missing:
    ///   1. LoadPreviousSage50Paths() - every path explicitly saved through this
    ///      tab (RecordSage50Path, on every "Save Sage 50 settings" click).
    ///   2. Sage50PathStateService.GetAllKnownPaths() - every path state.db has
    ///      ever recorded real sync activity against (same source the Customer
    ///      Refresh/History &amp; Logs path pickers use). Catches a path that was
    ///      genuinely connected to and used for real runs, but never (re-)saved
    ///      through THIS tab specifically - e.g. one only ever configured by
    ///      hand-editing appsettings.Local.json, or configured before this
    ///      dropdown feature existed.
    /// Plus whatever's currently configured, so it's never missing from its own
    /// dropdown either way. Preserves the current typed/selected text across the
    /// rebuild - Items.Clear() alone would otherwise blank the combo.</summary>
    private void RefreshSage50CompanyDataPathDropdown()
    {
        var currentText = _sage50CompanyDataPath.Text;

        var paths = LoadPreviousSage50Paths();
        if (!string.IsNullOrWhiteSpace(_syncStateDatabasePath?.Text))
        {
            foreach (var known in Sage50PathStateService.GetAllKnownPaths(_syncStateDatabasePath.Text))
            {
                if (!paths.Any(p => string.Equals(p, known, StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(known);
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(currentText) &&
            !paths.Any(p => string.Equals(p, currentText, StringComparison.OrdinalIgnoreCase)))
        {
            paths.Insert(0, currentText);
        }

        // Unsubscribed around Items.Clear() - clearing a ComboBox's Items resets
        // SelectedIndex to -1, which would otherwise fire OnSage50PathSelected and
        // try to load a config snapshot on every ordinary tab refresh/config
        // reload, not just when the operator actually picks a path from the list.
        _sage50CompanyDataPath.SelectedIndexChanged -= OnSage50CompanyDataPathSelectedIndexChanged;
        _sage50CompanyDataPath.Items.Clear();
        foreach (var path in paths) _sage50CompanyDataPath.Items.Add(path);
        _sage50CompanyDataPath.Text = currentText;
        _sage50CompanyDataPath.SelectedIndexChanged += OnSage50CompanyDataPathSelectedIndexChanged;
    }

    private void OnSage50CompanyDataPathSelectedIndexChanged(object? sender, EventArgs e) => OnSage50PathSelected();

    /// <summary>Restores the rest of the Sage 50 tab (username, password, account
    /// defaults, tax codes, charge account map) from whatever was last saved for
    /// the newly-selected path - confirmed 2026-08-24 this is needed because
    /// appsettings.json/appsettings.Local.json only ever hold ONE current
    /// configuration, overwritten in place on every Save regardless of which path
    /// it was actually for, so switching between two Sage 50 company files used to
    /// mean re-entering everything else by hand every time. Only fires from an
    /// actual dropdown pick (see the unsubscribe/resubscribe in
    /// RefreshSage50CompanyDataPathDropdown), not from typing a new path - a path
    /// with no saved snapshot yet (brand new, or never saved through this app)
    /// just leaves every other field as it currently is.
    ///
    /// Also updates the top bar's "Target Sage50" banner immediately, to exactly
    /// whatever was just picked - confirmed live 2026-08-24 this was expected the
    /// instant a different path is selected, not only after a subsequent Save.
    /// This is a deliberate, narrow exception to RefreshGlobalTargetSage50Label's
    /// usual rule (only ever showing the SAVED path, not a live unsaved edit,
    /// specifically so a confirmation dialog can never show something different
    /// from what a run will really use) - safe here because every confirmation
    /// dialog that actually starts a run reads the SAVED path directly (see
    /// CurrentConfiguredSage50Path), never this banner, so an optimistic display
    /// update here can't reintroduce that mismatch.</summary>
    private void OnSage50PathSelected()
    {
        var path = _sage50CompanyDataPath.Text;
        if (string.IsNullOrWhiteSpace(path)) return;

        _globalTargetSage50Label.Text = $"Target Sage50: {path}";

        if (_syncStateDatabasePath is null || string.IsNullOrWhiteSpace(_syncStateDatabasePath.Text)) return;

        var snapshot = Sage50ConfigSnapshotService.TryLoadSnapshot(_syncStateDatabasePath.Text, path);
        if (snapshot is null) return;

        _sage50UserName.Text = snapshot.UserName;
        _sage50Password.Text = snapshot.Password;
        _sage50AppName.Text = snapshot.AppName;
        _sage50AppId.Text = snapshot.AppId;
        _sage50ExpectedSdkVersion.Text = snapshot.ExpectedSdkVersion;
        _sage50DefaultRevenueAccount.Text = snapshot.DefaultRevenueAccount;
        _sage50DefaultReceivableAccount.Text = snapshot.DefaultReceivableAccount;
        _sage50DefaultNetTermDays.Value = Math.Clamp(snapshot.DefaultNetTermDays, _sage50DefaultNetTermDays.Minimum, _sage50DefaultNetTermDays.Maximum);
        _sage50AutoCreateCustomers.Checked = snapshot.AutoCreateCustomers;
        _sage50SyncCustomerUpdates.Checked = snapshot.SyncCustomerUpdatesFromPortPro;
        _sage50AutoCreateItems.Checked = snapshot.AutoCreateItems;
        _sage50DryRun.Checked = snapshot.DryRun;
        _sage50IgnoreAccountMismatchUseDefault.Checked = snapshot.IgnoreAccountMismatchUseDefault;
        _sage50AccountsUnverifiable.Text = snapshot.AccountsUnverifiableBySdk;

        _taxCodesGrid.Rows.Clear();
        foreach (var row in snapshot.TaxCodes) _taxCodesGrid.Rows.Add(row.Abbreviation, row.Sage50Code);

        _chargeAccountMapGrid.Rows.Clear();
        foreach (var row in snapshot.ChargeAccountMap)
        {
            _chargeAccountMapGrid.Rows.Add(row.PortProChargeName, row.PortProChargeNumber, row.Sage50AccountName, row.Sage50AccountNumber);
        }
    }

    /// <summary>Mirrors the exact set of fields RefreshSage50Tab/SaveSage50Tab
    /// already read - see Sage50ConfigSnapshotService's doc comment for why this
    /// is captured per-path rather than relying on appsettings.json/
    /// appsettings.Local.json alone.</summary>
    private Sage50ConfigSnapshotService.Snapshot BuildSage50ConfigSnapshotFromForm()
    {
        var snapshot = new Sage50ConfigSnapshotService.Snapshot
        {
            UserName = _sage50UserName.Text,
            Password = _sage50Password.Text,
            AppName = _sage50AppName.Text,
            AppId = _sage50AppId.Text,
            ExpectedSdkVersion = _sage50ExpectedSdkVersion.Text,
            DefaultRevenueAccount = _sage50DefaultRevenueAccount.Text,
            DefaultReceivableAccount = _sage50DefaultReceivableAccount.Text,
            DefaultNetTermDays = (int)_sage50DefaultNetTermDays.Value,
            AutoCreateCustomers = _sage50AutoCreateCustomers.Checked,
            SyncCustomerUpdatesFromPortPro = _sage50SyncCustomerUpdates.Checked,
            AutoCreateItems = _sage50AutoCreateItems.Checked,
            DryRun = _sage50DryRun.Checked,
            IgnoreAccountMismatchUseDefault = _sage50IgnoreAccountMismatchUseDefault.Checked,
            AccountsUnverifiableBySdk = _sage50AccountsUnverifiable.Text
        };

        foreach (DataGridViewRow r in _taxCodesGrid.Rows)
        {
            if (r.IsNewRow) continue;
            var abbreviation = r.Cells["Abbreviation"].Value?.ToString();
            if (string.IsNullOrWhiteSpace(abbreviation)) continue;
            snapshot.TaxCodes.Add(new Sage50ConfigSnapshotService.TaxCodeRow
            {
                Abbreviation = abbreviation,
                Sage50Code = r.Cells["Sage50Code"].Value?.ToString() ?? ""
            });
        }

        foreach (DataGridViewRow r in _chargeAccountMapGrid.Rows)
        {
            if (r.IsNewRow) continue;
            var name = r.Cells["PortProChargeName"].Value?.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            snapshot.ChargeAccountMap.Add(new Sage50ConfigSnapshotService.ChargeMapRow
            {
                PortProChargeName = name,
                PortProChargeNumber = r.Cells["PortProChargeNumber"].Value?.ToString() ?? "",
                Sage50AccountName = r.Cells["Sage50AccountName"].Value?.ToString() ?? "",
                Sage50AccountNumber = r.Cells["Sage50AccountNumber"].Value?.ToString() ?? ""
            });
        }

        return snapshot;
    }

    private void RefreshSage50Tab()
    {
        if (_appSettings is null) return;

        _sage50CompanyDataPath.Text = _localSettings?.GetString("PortProSage.Sage50.CompanyDataPath")
            ?? _appSettings.GetString("PortProSage.Sage50.CompanyDataPath");
        RefreshSage50CompanyDataPathDropdown();
        _sage50UserName.Text = _localSettings?.GetString("PortProSage.Sage50.UserName")
            ?? _appSettings.GetString("PortProSage.Sage50.UserName");
        _sage50Password.Text = _localSettings?.GetString("PortProSage.Sage50.Password") ?? "";
        _sage50AppName.Text = _appSettings.GetString("PortProSage.Sage50.AppName");
        _sage50AppId.Text = _appSettings.GetString("PortProSage.Sage50.AppId");
        _sage50ExpectedSdkVersion.Text = _appSettings.GetString("PortProSage.Sage50.ExpectedSdkVersion");
        _sage50DefaultRevenueAccount.Text = _appSettings.GetString("PortProSage.Sage50.DefaultRevenueAccount");
        _sage50DefaultReceivableAccount.Text = _appSettings.GetString("PortProSage.Sage50.DefaultReceivableAccount");
        _sage50DefaultNetTermDays.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Sage50.DefaultNetTermDays", 30), _sage50DefaultNetTermDays.Minimum, _sage50DefaultNetTermDays.Maximum);
        _sage50AutoCreateCustomers.Checked = _appSettings.GetBool("PortProSage.Sage50.AutoCreateCustomers");
        _sage50SyncCustomerUpdates.Checked = _appSettings.GetBool("PortProSage.Sage50.SyncCustomerUpdatesFromPortPro", true);
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

    /// <summary>showConfirmation=false is used by TestSage50Connection - it saves
    /// silently right before testing (confirmed live 2026-08-24: the test should
    /// always reflect what's currently in the fields, not require a separate
    /// manual Save first), where a "Saved" popup would just be a redundant extra
    /// click before the actual connection-test confirmation.</summary>
    private void SaveSage50Tab(bool showConfirmation = true)
    {
        if (_appSettings is null || _localSettings is null) return;

        _appSettings.SetString("PortProSage.Sage50.AppName", _sage50AppName.Text);
        _appSettings.SetString("PortProSage.Sage50.AppId", _sage50AppId.Text);
        _appSettings.SetString("PortProSage.Sage50.ExpectedSdkVersion", _sage50ExpectedSdkVersion.Text);
        _appSettings.SetString("PortProSage.Sage50.DefaultRevenueAccount", _sage50DefaultRevenueAccount.Text);
        _appSettings.SetString("PortProSage.Sage50.DefaultReceivableAccount", _sage50DefaultReceivableAccount.Text);
        _appSettings.SetInt("PortProSage.Sage50.DefaultNetTermDays", (int)_sage50DefaultNetTermDays.Value);
        _appSettings.SetBool("PortProSage.Sage50.AutoCreateCustomers", _sage50AutoCreateCustomers.Checked);
        _appSettings.SetBool("PortProSage.Sage50.SyncCustomerUpdatesFromPortPro", _sage50SyncCustomerUpdates.Checked);
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

        RecordSage50Path(_sage50CompanyDataPath.Text);
        RefreshSage50CompanyDataPathDropdown();

        // Snapshot everything on this tab against the path it was actually saved
        // for, so picking this same path again later (even after switching to a
        // different one in between) restores it - see Sage50ConfigSnapshotService.
        if (!string.IsNullOrWhiteSpace(_syncStateDatabasePath?.Text) && !string.IsNullOrWhiteSpace(_sage50CompanyDataPath.Text))
        {
            Sage50ConfigSnapshotService.SaveSnapshot(_syncStateDatabasePath.Text, _sage50CompanyDataPath.Text, BuildSage50ConfigSnapshotFromForm());
        }

        // Confirmed live 2026-08-24: without this, the top bar's "Target Sage50"
        // banner (and Customer Refresh's/History & Logs' own path dropdowns) kept
        // showing whatever was true before this save, indefinitely, until some
        // unrelated full config reload happened to run - a real mismatch between
        // what the Sage 50 tab said and what the rest of the app showed.
        RefreshGlobalTargetSage50Label();

        if (showConfirmation)
        {
            MessageBox.Show(this, "Sage 50 settings saved. The running Service needs a restart to pick up changes.", "Saved",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
