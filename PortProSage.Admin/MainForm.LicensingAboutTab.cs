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
    private static readonly Color LicenseMutedText = Color.FromArgb(92, 102, 114);
    private static readonly Color LicenseLabelText = Color.FromArgb(136, 145, 155);

    /// <summary>Now the app's only About-style tab - the original, simpler About
    /// tab was removed in favor of this one. Licensed To/Location/License Type/
    /// License ID below are placeholder values - nothing on this tab reads from a
    /// real license file yet (see the licensing design discussion this followed
    /// from).
    ///
    /// Every section here is built from a single outer TableLayoutPanel (one
    /// column, each section its own AutoSize row), the same layout primitive the
    /// rest of this app already uses everywhere else (see NewFieldGrid) - not the
    /// nested FlowLayoutPanels an earlier version of this tab used, which
    /// produced visibly misaligned/overlapping cards (each card's own AutoSize
    /// height came out short, so the next section started before the previous
    /// one's content actually ended). Every section is Anchor=Left|Right so it
    /// always stretches to exactly the same width as every other section -
    /// that's what makes the cards line up, rather than guessing/hardcoding a
    /// pixel width for each one. Card corners/shadows are still approximated with
    /// plain bordered Panels, not the rounded-corner/shadow cards from the
    /// original design mockup - stock WinForms panels are hard-cornered and
    /// shadow-free without owner-drawing, not worth the added complexity/risk
    /// here.</summary>
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

        // Dock=Top stretches this to the full width of `outer` automatically
        // (minus its Padding), and every section below is Anchor=Left|Right, so
        // the whole tab's width comes from exactly one place - resize the window
        // and every card resizes with it, staying aligned.
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddSection(Control control)
        {
            var row = root.RowCount++;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            root.Controls.Add(control, 0, row);
        }

        AddSection(LicensingHeader());
        AddSection(LicenseInformationCard());
        AddSection(CompanySupportRow());
        AddSection(OtherProductsCard());
        AddSection(AuthorizedUseBand());
        AddSection(new Label
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
            Margin = new Padding(0, 0, 0, 20)
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

        var textStack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        AddStackRow(textStack, new Label { Text = "PortProSage Sync", Font = new Font(Font.FontFamily, 15f, FontStyle.Bold), AutoSize = true });
        AddStackRow(textStack, new Label { Text = $"Version {AppVersion}", ForeColor = SystemColors.GrayText, AutoSize = true });
        row.Controls.Add(textStack);

        return row;
    }

    private Panel LicenseInformationCard()
    {
        var card = CardPanel();
        var content = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 2-column status row (fixed-size icon | text stack) - a TableLayoutPanel,
        // not a FlowLayoutPanel, so the row's height is correctly reserved as the
        // TALLER of the icon/text content (whichever that turns out to be),
        // instead of the icon's fixed Size silently overflowing past whatever a
        // FlowLayoutPanel guessed the row's height should be.
        var statusRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 18) };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.Controls.Add(new Label
        {
            Text = "✓",
            Font = new Font(Font.FontFamily, 14f, FontStyle.Bold),
            ForeColor = LicenseSuccessColor,
            BackColor = LicenseSuccessSoft,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(36, 36),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 12, 0)
        }, 0, 0);
        var statusTextStack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 2, 0, 0) };
        AddStackRow(statusTextStack, EyebrowLabel("LICENSE INFORMATION"));
        AddStackRow(statusTextStack, new Label { Text = "●  License Active", ForeColor = LicenseSuccessColor, Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        statusRow.Controls.Add(statusTextStack, 1, 0);
        content.Controls.Add(statusRow, 0, 0);

        // Percent columns, not a fixed pixel width - the fact grid always fills
        // exactly the card's actual width (which itself always matches every
        // other section's width - see AddSection), instead of leaving a growing
        // gap on the right on a wide window like a fixed-width grid would.
        var factGrid = new TableLayoutPanel { ColumnCount = 2, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        factGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        factGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        // Placeholders - no real license file/backend exists yet to read these from.
        factGrid.Controls.Add(FactCell("LICENSED TO", "RS Rush Transfer Xpress Inc."), 0, 0);
        factGrid.Controls.Add(FactCell("LOCATION", "Ontario, Canada"), 1, 0);
        factGrid.Controls.Add(FactCell("LICENSE TYPE", "Commercial — Single Site"), 0, 1);
        factGrid.Controls.Add(FactCell("PRODUCT", "PortProSage Sync"), 1, 1);
        factGrid.Controls.Add(FactCell("VERSION", AppVersion), 0, 2);
        factGrid.Controls.Add(FactCellWithCopy("LICENSE ID", "PPSS-••••-••••-7F3A", "PPSS-8C41-3D9E-7F3A"), 1, 2);
        content.Controls.Add(factGrid, 0, 1);

        card.Controls.Add(content);
        return card;
    }

    /// <summary>Company and Support side by side, each exactly half the width of
    /// every other section (Percent 50/50) - always sums to the same total width
    /// as the License card above and the Other Products/Authorized Use sections
    /// below, whatever the window's actual width is.</summary>
    private TableLayoutPanel CompanySupportRow()
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 16) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var company = CompanyCard();
        company.Dock = DockStyle.Fill;
        company.Margin = new Padding(0, 0, 8, 0);
        var support = SupportCard();
        support.Dock = DockStyle.Fill;
        support.Margin = new Padding(8, 0, 0, 0);

        row.Controls.Add(company, 0, 0);
        row.Controls.Add(support, 1, 0);
        return row;
    }

    private Panel CompanyCard()
    {
        var card = CardPanel();
        var stack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        AddStackRow(stack, SectionHeading("Company"));
        AddStackRow(stack, new Label { Text = "SmallArc Inc.", AutoSize = true, Margin = new Padding(0, 10, 0, 2) });
        AddStackRow(stack, new Label { Text = "Head Office: Edison, NJ (USA)", AutoSize = true, ForeColor = LicenseMutedText, Margin = new Padding(0, 0, 0, 10) });
        AddStackRow(stack, new Label
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
        var card = CardPanel();
        var stack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        AddStackRow(stack, SectionHeading("Support"));

        var emailRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 10, 0, 10) };
        emailRow.Controls.Add(new Label { Text = "contact@smallarc.com", AutoSize = true, Margin = new Padding(0, 4, 6, 0) });
        emailRow.Controls.Add(CreateCopyButton("contact@smallarc.com"));
        AddStackRow(stack, emailRow);

        AddStackRow(stack, new Label { Text = "United States — Main (US)", AutoSize = true, ForeColor = LicenseMutedText });
        AddStackRow(stack, new Label { Text = "(732) 929-7002", AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        AddStackRow(stack, new Label { Text = "Canada — Main (CA)", AutoSize = true, ForeColor = LicenseMutedText });
        AddStackRow(stack, new Label { Text = "(905) 863-4560", AutoSize = true });

        card.Controls.Add(stack);
        return card;
    }

    private Panel OtherProductsCard()
    {
        var card = CardPanel();
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

        row.Controls.Add(new Label
        {
            Text = "F",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            ForeColor = LicenseMutedText,
            BackColor = Color.FromArgb(241, 243, 246),
            TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(36, 36),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 14, 0)
        }, 0, 0);

        var textStack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 0, 0) };
        AddStackRow(textStack, new Label { Text = "Fixyee", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        AddStackRow(textStack, new Label { Text = "www.fixyee.com", ForeColor = LicenseMutedText, AutoSize = true });
        row.Controls.Add(textStack, 1, 0);

        var openSite = new Button
        {
            Text = "Open Website",
            Width = 130,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Left
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
        row.Controls.Add(openSite, 2, 0);

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
            Padding = new Padding(20, 16, 20, 16),
            Margin = new Padding(0, 0, 0, 16)
        };

        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));

        row.Controls.Add(new Label
        {
            Text = "This software is licensed and authorized by SmallArc Inc. for use exclusively at the " +
                   "licensed location and under the terms of the applicable licensing agreement. Unauthorized " +
                   "use, reproduction, distribution, or deployment without the express written consent of " +
                   "SmallArc Inc. is prohibited.",
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = LicenseMutedText,
            Font = new Font(Font.FontFamily, 8.5f),
            Margin = new Padding(0, 4, 20, 0)
        }, 0, 0);

        var termsButton = new Button
        {
            Text = "View License Terms",
            Width = 150,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Right
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
        row.Controls.Add(termsButton, 1, 0);

        band.Controls.Add(row);
        return band;
    }

    private static Panel CardPanel() => new()
    {
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.White,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(20, 18, 20, 18),
        Margin = new Padding(0, 0, 0, 16)
    };

    /// <summary>Adds `control` as the next row of a single-column, AutoSize
    /// TableLayoutPanel "stack" - the TableLayoutPanel equivalent of a vertical
    /// FlowLayoutPanel, used throughout this tab in place of one (see
    /// BuildLicensingAboutTab's doc comment for why).</summary>
    private static void AddStackRow(TableLayoutPanel stack, Control control)
    {
        var row = stack.RowCount++;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(control, 0, row);
    }

    private Label SectionHeading(string text) => new()
    {
        Text = text,
        Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
        ForeColor = SystemColors.ControlDarkDark,
        AutoSize = true
    };

    /// <summary>A "Copy" button that copies the given text to the clipboard,
    /// briefly flashing "Copied!" as feedback.</summary>
    private static Button CreateCopyButton(string textToCopy)
    {
        var button = new Button
        {
            Text = "Copy",
            Width = 56,
            Height = 24,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0),
            UseVisualStyleBackColor = true
        };

        var resetTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        resetTimer.Tick += (_, _) =>
        {
            button.Text = "Copy";
            resetTimer.Stop();
        };

        button.Click += (_, _) =>
        {
            Clipboard.SetText(textToCopy);
            button.Text = "Copied!";
            resetTimer.Stop();
            resetTimer.Start();
        };

        return button;
    }

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
        var stack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        AddStackRow(stack, EyebrowLabel(label));
        AddStackRow(stack, new Label { Text = value, Font = new Font("Segoe UI", 9.5f), AutoSize = true });
        panel.Controls.Add(stack);
        return panel;
    }

    private static Panel FactCellWithCopy(string label, string maskedValue, string copyValue)
    {
        var panel = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, 24, 16), Margin = new Padding(0) };
        var stack = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        AddStackRow(stack, EyebrowLabel(label));
        var valueRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true };
        valueRow.Controls.Add(new Label { Text = maskedValue, Font = new Font("Consolas", 9.5f), AutoSize = true, Margin = new Padding(0, 3, 8, 0) });
        valueRow.Controls.Add(CreateCopyButton(copyValue));
        AddStackRow(stack, valueRow);
        panel.Controls.Add(stack);
        return panel;
    }
}
