namespace PortProSage.Admin;

public partial class MainForm
{
    // Same restrained blue already used for the top bar's help icons
    // (HelpIconColor in MainForm.cs) - reused here rather than picking a new
    // accent, so the app doesn't end up with two different "blue"s.
    private static readonly Color LicenseAccentColor = Color.FromArgb(33, 88, 168);
    private static readonly Color LicenseAccentSoft = Color.FromArgb(234, 241, 251);
    private static readonly Color LicenseSuccessColor = Color.FromArgb(28, 138, 87);
    private static readonly Color LicenseSuccessSoft = Color.FromArgb(231, 246, 238);
    private static readonly Color LicenseCardBorder = Color.FromArgb(225, 229, 234);
    private static readonly Color LicenseMutedText = Color.FromArgb(92, 102, 114);
    private static readonly Color LicenseLabelText = Color.FromArgb(136, 145, 155);

    /// <summary>New, standalone tab previewing the modernized License &amp; About
    /// design - deliberately kept SEPARATE from BuildAboutTab() rather than
    /// replacing it (per explicit direction), until the real licensing backend
    /// exists and this is ready to become the actual About tab. Licensed To/
    /// Location/License Type/License ID below are placeholder values - nothing
    /// on this tab reads from a real license file yet (see the licensing design
    /// discussion this followed from).
    ///
    /// Card look is approximated with plain bordered Panels, not the rounded-
    /// corner/shadow cards from the design mockup - stock WinForms panels are
    /// hard-cornered and shadow-free without owner-drawing, which wasn't worth
    /// the added complexity/risk for a preview tab. Layout, spacing, hierarchy,
    /// and the color palette all carry over as designed.</summary>
    private TabPage BuildLicensingAboutTab()
    {
        var page = new TabPage("Licensing & About");
        var outer = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(32, 28, 32, 28),
            BackColor = Color.FromArgb(243, 245, 248)
        };

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        root.Controls.Add(LicensingHeader());
        root.Controls.Add(LicenseInformationCard());
        root.Controls.Add(LicensingTwoUpRow(CompanyCard(), SupportCard()));
        root.Controls.Add(OtherProductsCard());
        root.Controls.Add(AuthorizedUseBand());
        root.Controls.Add(new Label
        {
            Text = "© 2026 SmallArc Inc. • All rights reserved",
            AutoSize = true,
            ForeColor = LicenseLabelText,
            Font = new Font(Font.FontFamily, 8f),
            Margin = new Padding(4, 4, 0, 0)
        });

        outer.Controls.Add(root);
        page.Controls.Add(outer);
        return page;
    }

    private FlowLayoutPanel LicensingHeader()
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 18)
        };

        row.Controls.Add(new Label
        {
            Text = "PS",
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            ForeColor = LicenseAccentColor,
            BackColor = LicenseAccentSoft,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(40, 40),
            Margin = new Padding(0, 0, 12, 0)
        });

        var textStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        textStack.Controls.Add(new Label
        {
            Text = "PortProSage Sync",
            Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
            AutoSize = true
        });
        textStack.Controls.Add(new Label
        {
            Text = $"Version {AppVersion}",
            ForeColor = SystemColors.GrayText,
            AutoSize = true
        });
        row.Controls.Add(textStack);

        return row;
    }

    private Panel LicenseInformationCard()
    {
        var card = CardPanel(680);
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };

        var statusRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 16) };
        statusRow.Controls.Add(new Label
        {
            Text = "✓",
            Font = new Font(Font.FontFamily, 14f, FontStyle.Bold),
            ForeColor = LicenseSuccessColor,
            BackColor = LicenseSuccessSoft,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(36, 36),
            Margin = new Padding(0, 0, 12, 0)
        });
        var statusTextStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        statusTextStack.Controls.Add(EyebrowLabel("LICENSE INFORMATION"));
        statusTextStack.Controls.Add(new Label
        {
            Text = "●  License Active",
            ForeColor = LicenseSuccessColor,
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true
        });
        statusRow.Controls.Add(statusTextStack);
        stack.Controls.Add(statusRow);

        var factGrid = new TableLayoutPanel { ColumnCount = 2, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        factGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        factGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        // Placeholders - no real license file/backend exists yet to read these from.
        factGrid.Controls.Add(FactCell("LICENSED TO", "RS Rush Transfer Xpress Inc."), 0, 0);
        factGrid.Controls.Add(FactCell("LOCATION", "Ontario, Canada"), 1, 0);
        factGrid.Controls.Add(FactCell("LICENSE TYPE", "Commercial — Single Site"), 0, 1);
        factGrid.Controls.Add(FactCell("PRODUCT", "PortProSage Sync"), 1, 1);
        factGrid.Controls.Add(FactCell("VERSION", AppVersion), 0, 2);
        factGrid.Controls.Add(FactCellWithCopy("LICENSE ID", "PPSS-••••-••••-7F3A", "PPSS-8C41-3D9E-7F3A"), 1, 2);
        stack.Controls.Add(factGrid);

        card.Controls.Add(stack);
        return card;
    }

    private Panel CompanyCard()
    {
        var card = CardPanel(320);
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        stack.Controls.Add(SectionHeading("Company"));
        stack.Controls.Add(new Label { Text = "SmallArc Inc.", AutoSize = true, Margin = new Padding(0, 10, 0, 2) });
        stack.Controls.Add(new Label { Text = "Head Office: Edison, NJ (USA)", AutoSize = true, ForeColor = LicenseMutedText, Margin = new Padding(0, 0, 0, 10) });
        stack.Controls.Add(new Label
        {
            Text = "Professional software & integration services for logistics and freight operations.",
            AutoSize = true,
            MaximumSize = new Size(270, 0),
            ForeColor = LicenseMutedText
        });
        card.Controls.Add(stack);
        return card;
    }

    private Panel SupportCard()
    {
        var card = CardPanel(320);
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        stack.Controls.Add(SectionHeading("Support"));

        var emailRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 10, 0, 10) };
        emailRow.Controls.Add(new Label { Text = "contact@smallarc.com", AutoSize = true, Margin = new Padding(0, 4, 6, 0) });
        emailRow.Controls.Add(CreateCopyButton("contact@smallarc.com"));
        stack.Controls.Add(emailRow);

        stack.Controls.Add(new Label { Text = "United States — Main (US)", AutoSize = true, ForeColor = LicenseMutedText, Margin = new Padding(0, 0, 0, 0) });
        stack.Controls.Add(new Label { Text = "(732) 929-7002", AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        stack.Controls.Add(new Label { Text = "Canada — Main (CA)", AutoSize = true, ForeColor = LicenseMutedText, Margin = new Padding(0, 0, 0, 0) });
        stack.Controls.Add(new Label { Text = "(905) 863-4560", AutoSize = true, Margin = new Padding(0, 0, 0, 0) });

        card.Controls.Add(stack);
        return card;
    }

    private Panel OtherProductsCard()
    {
        var card = CardPanel(680);
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };

        row.Controls.Add(new Label
        {
            Text = "F",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            ForeColor = LicenseMutedText,
            BackColor = Color.FromArgb(241, 243, 246),
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(36, 36),
            Margin = new Padding(0, 0, 14, 0)
        });

        var textStack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 2, 24, 0) };
        textStack.Controls.Add(new Label { Text = "Fixyee", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        textStack.Controls.Add(new Label { Text = "www.fixyee.com", ForeColor = LicenseMutedText, AutoSize = true });
        row.Controls.Add(textStack);

        var openSite = new Button
        {
            Text = "Open Website",
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 6, 0, 0)
        };
        openSite.FlatAppearance.BorderColor = LicenseAccentColor;
        openSite.ForeColor = LicenseAccentColor;
        openSite.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://www.fixyee.com", UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not open the website:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        row.Controls.Add(openSite);

        card.Controls.Add(row);
        return card;
    }

    private Panel AuthorizedUseBand()
    {
        var band = new Panel
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(248, 249, 251),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(680, 0),
            Padding = new Padding(20, 16, 20, 16),
            Margin = new Padding(0, 0, 0, 16)
        };

        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
        row.Controls.Add(new Label
        {
            Text = "This software is licensed and authorized by SmallArc Inc. for use exclusively at the " +
                   "licensed location and under the terms of the applicable licensing agreement. Unauthorized " +
                   "use, reproduction, distribution, or deployment without the express written consent of " +
                   "SmallArc Inc. is prohibited.",
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            ForeColor = LicenseMutedText,
            Font = new Font(Font.FontFamily, 8.5f),
            Margin = new Padding(0, 4, 20, 0)
        });

        var termsButton = new Button
        {
            Text = "View License Terms",
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 0)
        };
        termsButton.FlatAppearance.BorderColor = LicenseAccentColor;
        termsButton.ForeColor = LicenseAccentColor;
        // No standalone terms document exists yet - shows the same notice text
        // in a dialog until a real one is written and this can open/link to it.
        termsButton.Click += (_, _) => MessageBox.Show(this,
            "This software is licensed and authorized by SmallArc Inc. for use exclusively at the licensed " +
            "location and under the terms of the applicable licensing agreement. Unauthorized use, reproduction, " +
            "distribution, or deployment without the express written consent of SmallArc Inc. is prohibited.",
            "License Terms", MessageBoxButtons.OK, MessageBoxIcon.Information);
        row.Controls.Add(termsButton);

        band.Controls.Add(row);
        return band;
    }

    private static FlowLayoutPanel LicensingTwoUpRow(Control left, Control right)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        left.Margin = new Padding(0, 0, 16, 16);
        right.Margin = new Padding(0, 0, 0, 16);
        row.Controls.Add(left);
        row.Controls.Add(right);
        return row;
    }

    private static Panel CardPanel(int minWidth) => new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(minWidth, 0),
        Padding = new Padding(20, 18, 20, 18),
        Margin = new Padding(0, 0, 0, 16)
    };

    private static Label EyebrowLabel(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
        ForeColor = LicenseLabelText,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 2)
    };

    private static Panel FactCell(string label, string value)
    {
        var panel = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, 24, 16), Margin = new Padding(0) };
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        stack.Controls.Add(EyebrowLabel(label));
        stack.Controls.Add(new Label { Text = value, Font = new Font("Segoe UI", 9.5f), AutoSize = true });
        panel.Controls.Add(stack);
        return panel;
    }

    private Panel FactCellWithCopy(string label, string maskedValue, string copyValue)
    {
        var panel = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, 24, 16), Margin = new Padding(0) };
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        stack.Controls.Add(EyebrowLabel(label));
        var valueRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
        valueRow.Controls.Add(new Label { Text = maskedValue, Font = new Font("Consolas", 9.5f), AutoSize = true, Margin = new Padding(0, 3, 8, 0) });
        valueRow.Controls.Add(CreateCopyButton(copyValue));
        stack.Controls.Add(valueRow);
        panel.Controls.Add(stack);
        return panel;
    }
}
