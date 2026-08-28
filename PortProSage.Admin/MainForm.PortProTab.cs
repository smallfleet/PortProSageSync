namespace PortProSage.Admin;

public partial class MainForm
{
    private TextBox _portProBaseUrl = new() { Width = FieldHalfWidth };
    private TextBox _portProInvoiceEndpoint = new() { Width = FieldHalfWidth };
    private TextBox _portProCustomerEndpoint = new() { Width = FieldHalfWidth };
    private TextBox _portProAccessTokenEndpoint = new() { Width = FieldHalfWidth };
    private TextBox _portProNewTokenEndpoint = new() { Width = FieldHalfWidth };
    // Width 20% wider than the usual FieldHalfWidth - confirmed live 2026-08-26 a
    // real PortPro token (a JWT) is long enough that the normal width clipped it
    // badly even with the reveal toggle below.
    private const int SecretFieldWidth = (int)(FieldHalfWidth * 1.2);
    private TextBox _portProAccessToken = new() { UseSystemPasswordChar = true, Width = SecretFieldWidth };
    private TextBox _portProRefreshToken = new() { UseSystemPasswordChar = true, Width = SecretFieldWidth };
    private NumericUpDown _portProPageSize = new() { Minimum = 1, Maximum = 1000 };
    private NumericUpDown _portProTimeoutSeconds = new() { Minimum = 1, Maximum = 600 };

    private TabPage BuildPortProTab()
    {
        var page = new TabPage("PortPro");
        var grid = NewFieldGrid();
        const string f = AppSettingsFileName;

        AddRow(grid, "Base URL", _portProBaseUrl, f, "PortProSage:PortPro:BaseUrl",
            "The root web address of PortPro's API - every other PortPro call is built by adding a path onto this.\n\n" +
            "Example: https://api1.app.portpro.io/v1\n\n" +
            "You'd only ever change this if PortPro moved their API to a different address.",
            stretchInput: false);
        AddRow(grid, "Invoice endpoint", _portProInvoiceEndpoint, f, "PortProSage:PortPro:InvoiceEndpoint",
            "The path (added onto Base URL) used to fetch invoices from PortPro.\n\n" +
            "Example: /invoices\n" +
            "Combined with Base URL this becomes: https://api1.app.portpro.io/v1/invoices\n\n" +
            "This is where every sync run actually pulls invoice data from.",
            stretchInput: false);
        AddRow(grid, "Customer endpoint", _portProCustomerEndpoint, f, "PortProSage:PortPro:CustomerEndpoint",
            "The path used to fetch PortPro's FULL customer profile (address, billing email, contact, payment " +
            "terms) - a separate, richer object than the lightweight \"caller\" info embedded on each invoice. " +
            "Used when auto-creating a new Sage 50 customer, and by the periodic customer-update sync (see the " +
            "Sage 50 tab's \"Update Customer with latest changes in PortPro\").\n\nExample: /customer",
            stretchInput: false);
        AddRow(grid, "Access token endpoint", _portProAccessTokenEndpoint, f, "PortProSage:PortPro:AccessTokenEndpoint",
            "The path used for the standard OAuth-style access token exchange. Reference/legacy field - " +
            "this account's real login flow uses 'New token endpoint' below instead.\n\nExample: /token",
            stretchInput: false);
        AddRow(grid, "New token endpoint", _portProNewTokenEndpoint, f, "PortProSage:PortPro:NewTokenEndpoint",
            "The path actually used to get a fresh access token from PortPro, using the Refresh token (secret) below as " +
            "authorization. Called automatically whenever the current access token expires or is rejected - you never " +
            "trigger this by hand.\n\nExample: /generate-new-token",
            stretchInput: false);
        AddRow(grid, "Page size", _portProPageSize, f, "PortProSage:PortPro:PageSize",
            "How many invoices PortPro returns per page when fetching a list. The sync process automatically pages " +
            "through everything - this just controls the chunk size of each request.\n\n" +
            "Example: 100 means invoice #1-100 come back in the first request, #101-200 in the second, and so on.");
        AddRow(grid, "Timeout (seconds)", _portProTimeoutSeconds, f, "PortProSage:PortPro:TimeoutSeconds",
            "How long to wait for PortPro to respond before giving up on a single request and treating it as failed.\n\n" +
            "Example: 60 means if PortPro takes longer than 60 seconds to answer one request, that request is abandoned.");
        AddSecretRowWithReveal(grid, "Access token (secret)", _portProAccessToken, "PortProSage:PortPro:AccessToken",
            "The current short-lived credential used to authenticate every PortPro API call. It's normally refreshed " +
            "automatically and re-saved here when it expires - you usually don't need to touch this by hand, except " +
            "the very first time you set this install up (paste the value PortPro's integration screen gives you).\n\n" +
            "\"Test Connection\" attempts a real fetch (last 24 hours of changed invoices) using whatever is " +
            "currently SAVED to appsettings.Local.json - Save PortPro settings first if you just changed something.\n\n" +
            "Click the 👁 button to reveal the actual value - useful for confirming a pasted token is exactly right " +
            "(no stray leading/trailing space, no missing characters) before saving.",
            ("Test Connection", (_, _) => TestPortProConnection()));
        AddSecretRowWithReveal(grid, "Refresh token (secret)", _portProRefreshToken, "PortProSage:PortPro:RefreshToken",
            "The longer-lived credential used to obtain a brand new Access token once the old one expires - this is " +
            "what actually keeps the connection working long-term without you re-entering anything. Get the real " +
            "value from PortPro's own integration/API settings screen.\n\n" +
            "Click the 👁 button to reveal the actual value - useful for confirming a pasted token is exactly right " +
            "(no stray leading/trailing space, no missing characters) before saving.");

        var save = new Button { Text = "Save PortPro settings" };
        save.Click += (_, _) => SavePortProTab();
        var saveBar = CreateActionButtonBar(save);

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(saveBar);

        RefreshAllTabsFromConfig += RefreshPortProTab;
        return page;
    }

    /// <summary>Like AddRowWithButton, but for a masked secret field: adds a 👁
    /// reveal-toggle button right after the field (confirmed live 2026-08-26 - a
    /// long JWT pasted into a masked box has no way to spot-check for a stray
    /// space or a truncated paste without this), plus an optional extra button
    /// (Access token's "Test Connection") alongside it.</summary>
    private void AddSecretRowWithReveal(TableLayoutPanel grid, string labelText, TextBox input, string jsonPath,
        string helpText, (string Text, EventHandler OnClick)? extraButton = null)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };

        input.Anchor = AnchorStyles.Left;
        input.Margin = new Padding(3, 4, 3, 4);

        var revealButton = new Button { Text = "👁", Width = 32, Height = 23, Margin = new Padding(6, 5, 3, 3) };
        var tooltip = new ToolTip();
        tooltip.SetToolTip(revealButton, "Show value");
        revealButton.Click += (_, _) =>
        {
            input.UseSystemPasswordChar = !input.UseSystemPasswordChar;
            tooltip.SetToolTip(revealButton, input.UseSystemPasswordChar ? "Show value" : "Hide value");
        };

        var wrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        wrap.Controls.Add(input);
        wrap.Controls.Add(revealButton);
        if (extraButton is { } extra)
        {
            var button = new Button { Text = extra.Text, AutoSize = true, Height = 23, Margin = new Padding(3, 5, 3, 3) };
            button.Click += extra.OnClick;
            wrap.Controls.Add(button);
        }
        if (!string.IsNullOrEmpty(helpText))
        {
            wrap.Controls.Add(CreateHelpIcon(labelText.Replace("\n", " "), helpText));
        }

        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(wrap, 1, row);
        WireSource(input, LocalSettingsFileName, jsonPath);
    }

    private void RefreshPortProTab()
    {
        if (_appSettings is null) return;
        _portProBaseUrl.Text = _appSettings.GetString("PortProSage.PortPro.BaseUrl");
        _portProInvoiceEndpoint.Text = _appSettings.GetString("PortProSage.PortPro.InvoiceEndpoint");
        _portProCustomerEndpoint.Text = _appSettings.GetString("PortProSage.PortPro.CustomerEndpoint");
        _portProAccessTokenEndpoint.Text = _appSettings.GetString("PortProSage.PortPro.AccessTokenEndpoint");
        _portProNewTokenEndpoint.Text = _appSettings.GetString("PortProSage.PortPro.NewTokenEndpoint");
        _portProPageSize.Value = Math.Clamp(_appSettings.GetInt("PortProSage.PortPro.PageSize", 100), _portProPageSize.Minimum, _portProPageSize.Maximum);
        _portProTimeoutSeconds.Value = Math.Clamp(_appSettings.GetInt("PortProSage.PortPro.TimeoutSeconds", 60), _portProTimeoutSeconds.Minimum, _portProTimeoutSeconds.Maximum);
        _portProAccessToken.Text = _localSettings?.GetString("PortProSage.PortPro.AccessToken") ?? "";
        _portProRefreshToken.Text = _localSettings?.GetString("PortProSage.PortPro.RefreshToken") ?? "";
    }

    private void SavePortProTab()
    {
        if (_appSettings is null || _localSettings is null) return;
        _appSettings.SetString("PortProSage.PortPro.BaseUrl", _portProBaseUrl.Text);
        _appSettings.SetString("PortProSage.PortPro.InvoiceEndpoint", _portProInvoiceEndpoint.Text);
        _appSettings.SetString("PortProSage.PortPro.CustomerEndpoint", _portProCustomerEndpoint.Text);
        _appSettings.SetString("PortProSage.PortPro.AccessTokenEndpoint", _portProAccessTokenEndpoint.Text);
        _appSettings.SetString("PortProSage.PortPro.NewTokenEndpoint", _portProNewTokenEndpoint.Text);
        _appSettings.SetInt("PortProSage.PortPro.PageSize", (int)_portProPageSize.Value);
        _appSettings.SetInt("PortProSage.PortPro.TimeoutSeconds", (int)_portProTimeoutSeconds.Value);
        _appSettings.Save();

        _localSettings.SetString("PortProSage.PortPro.AccessToken", _portProAccessToken.Text);
        _localSettings.SetString("PortProSage.PortPro.RefreshToken", _portProRefreshToken.Text);
        _localSettings.Save();

        MessageBox.Show(this, "PortPro settings saved. The running Service needs a restart to pick up changes.", "Saved",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
