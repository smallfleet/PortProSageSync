namespace PortProSage.Admin;

public partial class MainForm
{
    /// <summary>Last tab - static company/contact info, so anyone who ends up
    /// using this app (now or years from now) can see who built it and how to
    /// reach out, without having to dig through source control or old emails.</summary>
    private TabPage BuildAboutTab()
    {
        var page = new TabPage("About");
        var outer = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(32, 28, 32, 28) };

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };

        stack.Controls.Add(new Label
        {
            Text = "PortProSage Sync",
            Font = new Font(Font.FontFamily, 18f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        });
        stack.Controls.Add(new Label
        {
            Text = $"Version {AppVersion}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        });

        stack.Controls.Add(new Label
        {
            Text = "Made by SmallArc Inc.",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        });
        stack.Controls.Add(new Label
        {
            Text = "Head Office: Edison, NJ (USA)",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 22)
        });

        stack.Controls.Add(SectionHeading("Phone"));
        stack.Controls.Add(new Label { Text = "United States", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        stack.Controls.Add(new Label { Text = "Main (US): (732) 929-7002", AutoSize = true, Margin = new Padding(14, 2, 0, 10) });
        stack.Controls.Add(new Label { Text = "Canada", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 0) });
        stack.Controls.Add(new Label { Text = "Main (CA): (905) 863-4560", AutoSize = true, Margin = new Padding(14, 2, 0, 22) });

        stack.Controls.Add(SectionHeading("Contact"));
        stack.Controls.Add(CopyableRow("contact@smallarc.com", "contact@smallarc.com", new Padding(0, 8, 0, 22)));

        stack.Controls.Add(SectionHeading("Other Products"));
        stack.Controls.Add(new Label { Text = "Fixyee", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        stack.Controls.Add(CopyableRow("www.fixyee.com", "www.fixyee.com", new Padding(14, 2, 0, 26)));

        var noticeBox = new Panel
        {
            BorderStyle = BorderStyle.FixedSingle,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0, 4, 0, 0)
        };
        var noticeStack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        noticeStack.Controls.Add(new Label
        {
            Text = "Authorized Use Notice",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        });
        noticeStack.Controls.Add(new Label
        {
            Text = "This software is licensed and authorized by SmallArc Inc. for use exclusively at the location, " +
                   "and under the terms, discussed in official communication with SmallArc Inc. Any unauthorized " +
                   "use, reproduction, distribution, or deployment of this software without the express written " +
                   "consent of SmallArc Inc. is strictly prohibited and constitutes a violation of the applicable " +
                   "licensing agreement.",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = SystemColors.ControlDarkDark,
            Font = new Font(Font.FontFamily, 8.5f, FontStyle.Italic)
        });
        noticeBox.Controls.Add(noticeStack);
        stack.Controls.Add(noticeBox);

        outer.Controls.Add(stack);
        page.Controls.Add(outer);
        return page;
    }

    private Label SectionHeading(string text) => new()
    {
        Text = text,
        Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
        ForeColor = SystemColors.ControlDarkDark,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 0)
    };

    /// <summary>A display label + a small "Copy" button that copies the given
    /// text to the clipboard, briefly flashing "Copied!" as feedback - used for
    /// both the contact email and the product website, so a visitor doesn't have
    /// to select/retype either by hand.</summary>
    private FlowLayoutPanel CopyableRow(string displayText, string copyText, Padding margin)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = margin
        };
        row.Controls.Add(new Label { Text = displayText, AutoSize = true, Margin = new Padding(14, 4, 6, 0) });
        row.Controls.Add(CreateCopyButton(copyText));
        return row;
    }

    private Button CreateCopyButton(string textToCopy)
    {
        var button = new Button
        {
            Text = "Copy",
            Width = 56,
            Height = 24,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 0, 0),
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
}
