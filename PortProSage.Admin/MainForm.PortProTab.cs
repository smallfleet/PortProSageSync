namespace PortProSage.Admin;

public partial class MainForm
{
    private TextBox _portProBaseUrl = new();
    private TextBox _portProInvoiceEndpoint = new();
    private TextBox _portProAccessTokenEndpoint = new();
    private TextBox _portProNewTokenEndpoint = new();
    private TextBox _portProAccessToken = new() { UseSystemPasswordChar = true };
    private TextBox _portProRefreshToken = new() { UseSystemPasswordChar = true };
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
            "You'd only ever change this if PortPro moved their API to a different address.");
        AddRow(grid, "Invoice endpoint", _portProInvoiceEndpoint, f, "PortProSage:PortPro:InvoiceEndpoint",
            "The path (added onto Base URL) used to fetch invoices from PortPro.\n\n" +
            "Example: /invoices\n" +
            "Combined with Base URL this becomes: https://api1.app.portpro.io/v1/invoices\n\n" +
            "This is where every sync run actually pulls invoice data from.");
        AddRow(grid, "Access token endpoint", _portProAccessTokenEndpoint, f, "PortProSage:PortPro:AccessTokenEndpoint",
            "The path used for the standard OAuth-style access token exchange. Reference/legacy field - " +
            "this account's real login flow uses 'New token endpoint' below instead.\n\nExample: /token");
        AddRow(grid, "New token endpoint", _portProNewTokenEndpoint, f, "PortProSage:PortPro:NewTokenEndpoint",
            "The path actually used to get a fresh access token from PortPro, using the Refresh token (secret) below as " +
            "authorization. Called automatically whenever the current access token expires or is rejected - you never " +
            "trigger this by hand.\n\nExample: /generate-new-token");
        AddRow(grid, "Page size", _portProPageSize, f, "PortProSage:PortPro:PageSize",
            "How many invoices PortPro returns per page when fetching a list. The sync process automatically pages " +
            "through everything - this just controls the chunk size of each request.\n\n" +
            "Example: 100 means invoice #1-100 come back in the first request, #101-200 in the second, and so on.");
        AddRow(grid, "Timeout (seconds)", _portProTimeoutSeconds, f, "PortProSage:PortPro:TimeoutSeconds",
            "How long to wait for PortPro to respond before giving up on a single request and treating it as failed.\n\n" +
            "Example: 60 means if PortPro takes longer than 60 seconds to answer one request, that request is abandoned.");
        AddRow(grid, "Access token (secret)", _portProAccessToken, LocalSettingsFileName, "PortProSage:PortPro:AccessToken",
            "The current short-lived credential used to authenticate every PortPro API call. It's normally refreshed " +
            "automatically and re-saved here when it expires - you usually don't need to touch this by hand, except " +
            "the very first time you set this install up (paste the value PortPro's integration screen gives you).");
        AddRow(grid, "Refresh token (secret)", _portProRefreshToken, LocalSettingsFileName, "PortProSage:PortPro:RefreshToken",
            "The longer-lived credential used to obtain a brand new Access token once the old one expires - this is " +
            "what actually keeps the connection working long-term without you re-entering anything. Get the real " +
            "value from PortPro's own integration/API settings screen.");

        var save = new Button { Text = "Save PortPro settings", Dock = DockStyle.Bottom, Height = 32 };
        save.Click += (_, _) => SavePortProTab();

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(save);

        RefreshAllTabsFromConfig += RefreshPortProTab;
        return page;
    }

    private void RefreshPortProTab()
    {
        if (_appSettings is null) return;
        _portProBaseUrl.Text = _appSettings.GetString("PortProSage.PortPro.BaseUrl");
        _portProInvoiceEndpoint.Text = _appSettings.GetString("PortProSage.PortPro.InvoiceEndpoint");
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
