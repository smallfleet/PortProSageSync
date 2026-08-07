namespace PortProSage.Admin;

public partial class MainForm
{
    private CheckBox _emailEnabled = new() { Text = "Enabled (send failed-transaction reports by email)" };
    private TextBox _emailSmtpHost = new() { Width = FieldHalfWidth };
    private NumericUpDown _emailSmtpPort = new() { Minimum = 1, Maximum = 65535, Value = 587 };
    private CheckBox _emailUseSsl = new() { Text = "Use SSL" };
    private TextBox _emailFromAddress = new() { Width = FieldHalfWidth };
    private TextBox _emailUsername = new() { Width = FieldHalfWidth };
    private TextBox _emailPassword = new() { UseSystemPasswordChar = true, Width = FieldHalfWidth };
    private TextBox _emailRecipients = new() { Width = FieldHalfWidth };

    private TabPage BuildEmailTab()
    {
        var page = new TabPage("Email");
        var grid = NewFieldGrid();
        const string f = AppSettingsFileName;

        AddCheckRow(grid, _emailEnabled, f, "PortProSage:Email:Enabled",
            "The master switch for this whole feature. When unchecked, a failed-transaction report is still saved as " +
            "a CSV file on disk (see Sync tab -> Failed transactions folder), but no email is ever sent - every other " +
            "field on this tab is ignored. Check this only once SMTP host/credentials below are filled in with real values.");
        AddRow(grid, "SMTP host", _emailSmtpHost, f, "PortProSage:Email:SmtpHost",
            "The address of the mail server used to send failed-transaction reports.\n\n" +
            "Example: smtp.office365.com  (Microsoft 365) or  smtp.gmail.com  (Gmail).",
            stretchInput: false);
        AddRow(grid, "SMTP port", _emailSmtpPort, f, "PortProSage:Email:SmtpPort",
            "The network port the SMTP host listens on.\n\nExample: 587 (the standard port for SSL/TLS-secured SMTP submission).");
        AddCheckRow(grid, _emailUseSsl, f, "PortProSage:Email:UseSsl",
            "Whether the connection to the SMTP host is encrypted. Almost every modern mail provider requires this " +
            "checked - only uncheck it if your mail server specifically documents that it needs a plain, unencrypted connection.");
        AddRow(grid, "From address", _emailFromAddress, f, "PortProSage:Email:FromAddress",
            "The email address that failed-transaction reports appear to be sent from.\n\n" +
            "Example: portprosync@rushtransfer.com",
            stretchInput: false);
        AddRow(grid, "Username", _emailUsername, f, "PortProSage:Email:Username",
            "The login username for the SMTP host above - for most providers this is the same as the From address, " +
            "but not always (e.g. some providers use a separate app-specific username).",
            stretchInput: false);
        AddRow(grid, "Password (secret)", _emailPassword, LocalSettingsFileName, "PortProSage:Email:Password",
            "The password (or app-specific password, for providers like Gmail/Microsoft 365 that require one instead " +
            "of your real account password) for the SMTP username above.",
            stretchInput: false);
        AddRow(grid, "Recipients\n(comma-separated)", _emailRecipients, f, "PortProSage:Email:RecipientAddressesCsv",
            "Every email address that should receive a failed-transaction report, separated by commas. Sent to ALL " +
            "of these addresses every time a sync run has at least one failure.\n\n" +
            "Example: ashwani@smallarc.com, accounting@rushtransfer.com",
            stretchInput: false);

        var save = new Button { Text = "Save Email settings", Dock = DockStyle.Bottom, Height = 32 };
        save.Click += (_, _) => SaveEmailTab();

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(save);

        RefreshAllTabsFromConfig += RefreshEmailTab;
        return page;
    }

    private void RefreshEmailTab()
    {
        if (_appSettings is null) return;
        _emailEnabled.Checked = _appSettings.GetBool("PortProSage.Email.Enabled");
        _emailSmtpHost.Text = _appSettings.GetString("PortProSage.Email.SmtpHost");
        _emailSmtpPort.Value = Math.Clamp(_appSettings.GetInt("PortProSage.Email.SmtpPort", 587), _emailSmtpPort.Minimum, _emailSmtpPort.Maximum);
        _emailUseSsl.Checked = _appSettings.GetBool("PortProSage.Email.UseSsl", true);
        _emailFromAddress.Text = _appSettings.GetString("PortProSage.Email.FromAddress");
        _emailUsername.Text = _appSettings.GetString("PortProSage.Email.Username");
        _emailPassword.Text = _localSettings?.GetString("PortProSage.Email.Password") ?? "";
        _emailRecipients.Text = _appSettings.GetString("PortProSage.Email.RecipientAddressesCsv");
    }

    private void SaveEmailTab()
    {
        if (_appSettings is null || _localSettings is null) return;
        _appSettings.SetBool("PortProSage.Email.Enabled", _emailEnabled.Checked);
        _appSettings.SetString("PortProSage.Email.SmtpHost", _emailSmtpHost.Text);
        _appSettings.SetInt("PortProSage.Email.SmtpPort", (int)_emailSmtpPort.Value);
        _appSettings.SetBool("PortProSage.Email.UseSsl", _emailUseSsl.Checked);
        _appSettings.SetString("PortProSage.Email.FromAddress", _emailFromAddress.Text);
        _appSettings.SetString("PortProSage.Email.Username", _emailUsername.Text);
        _appSettings.SetString("PortProSage.Email.RecipientAddressesCsv", _emailRecipients.Text);
        _appSettings.Save();

        _localSettings.SetString("PortProSage.Email.Password", _emailPassword.Text);
        _localSettings.Save();

        MessageBox.Show(this, "Email settings saved. The running Service needs a restart to pick up changes.", "Saved",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
