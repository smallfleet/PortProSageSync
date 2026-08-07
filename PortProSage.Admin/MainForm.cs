using System.Text.Json.Nodes;
using PortProSage.Admin.Models;
using PortProSage.Admin.Services;

namespace PortProSage.Admin;

/// <summary>
/// Reads/edits the Service's real appsettings.json + appsettings.Local.json and
/// can start a real run (by writing the same trigger-file the Service's Worker
/// already watches for) - this window never talks to PortPro or Sage 50 itself,
/// it only edits files and, on Start, drops a request file for the already-
/// running Service to pick up. Nothing happens on Save except a file write;
/// nothing happens at all until Start is pressed.
/// </summary>
public partial class MainForm : Form
{
    private readonly ToolStripStatusLabel _sourceLabel = new() { Text = "Click any field to see where it's stored." };
    private readonly TextBox _serviceFolderBox = new() { Width = 480 };

    private JsonFileEditor? _appSettings;
    private JsonFileEditor? _localSettings;
    private TabControl _tabs = null!;

    private const string AppSettingsFileName = "appsettings.json";
    private const string LocalSettingsFileName = "appsettings.Local.json";

    // ---- Run/Results state ----
    private string? _pendingRequestId;
    private string? _pendingProcessedFolder;
    private readonly System.Windows.Forms.Timer _resultPollTimer = new() { Interval = 2000 };

    public MainForm()
    {
        Text = "PortProSage Admin";
        Width = 1000;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        var statusStrip = new StatusStrip { Dock = DockStyle.Bottom };
        statusStrip.Items.Add(_sourceLabel);

        var topBar = BuildServiceFolderBar();

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildPortProTab());
        _tabs.TabPages.Add(BuildSage50Tab());
        _tabs.TabPages.Add(BuildEmailTab());
        _tabs.TabPages.Add(BuildSyncTab());
        _tabs.TabPages.Add(BuildRunTab()); // includes the Automatic/Manual run controls
        _tabs.TabPages.Add(BuildResultsTab());

        Controls.Add(_tabs);
        Controls.Add(topBar);
        Controls.Add(statusStrip);

        _resultPollTimer.Tick += ResultPollTimer_Tick;

        Load += (_, _) => TryLoadConfig();
    }

    // ---------------------------------------------------------------------
    // Service folder selection + config load
    // ---------------------------------------------------------------------

    private Panel BuildServiceFolderBar()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 36 };

        var label = new Label { Text = "Service folder:", AutoSize = true, Location = new Point(8, 10) };
        _serviceFolderBox.Location = new Point(100, 7);
        _serviceFolderBox.Text = GuessServiceFolder();

        var browse = new Button { Text = "Browse...", Location = new Point(590, 5), Width = 80 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _serviceFolderBox.Text };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                _serviceFolderBox.Text = dialog.SelectedPath;
                TryLoadConfig();
            }
        };

        var reload = new Button { Text = "Reload", Location = new Point(680, 5), Width = 80 };
        reload.Click += (_, _) => TryLoadConfig();

        panel.Controls.Add(label);
        panel.Controls.Add(_serviceFolderBox);
        panel.Controls.Add(browse);
        panel.Controls.Add(reload);
        return panel;
    }

    /// <summary>
    /// Checks the documented production publish target first (README "Build &amp;
    /// run"), then this dev machine's known build output - either way, the user
    /// can override and it's just a starting guess, not a hard assumption.
    /// </summary>
    private static string GuessServiceFolder()
    {
        var candidates = new[]
        {
            @"C:\PortProSageSync\bin",
            @"C:\PortProSageSync\PortProSage.Service\bin\Debug\net48",
            @"C:\PortProSageSync\PortProSage.Service\bin\Release\net48"
        };
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, AppSettingsFileName))) ?? candidates[0];
    }

    private void TryLoadConfig()
    {
        var folder = _serviceFolderBox.Text;
        var appSettingsPath = Path.Combine(folder, AppSettingsFileName);
        var localSettingsPath = Path.Combine(folder, LocalSettingsFileName);

        if (!File.Exists(appSettingsPath))
        {
            MessageBox.Show(this, $"Could not find {AppSettingsFileName} in:\n{folder}", "Config not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _appSettings = new JsonFileEditor(appSettingsPath);
        _localSettings = new JsonFileEditor(localSettingsPath); // optional - fine if it doesn't exist yet

        RefreshAllTabsFromConfig?.Invoke();
    }

    /// <summary>Wired by each Build*Tab() method to repopulate its controls after a (re)load.</summary>
    private Action? RefreshAllTabsFromConfig;

    // ---------------------------------------------------------------------
    // Shared field helpers - every editable control shows its file+JSON path
    // in the status bar, but ONLY when clicked/focused/toggled, never inline.
    // ---------------------------------------------------------------------

    private void ShowSource(string fileName, string jsonPath) =>
        _sourceLabel.Text = $"Source: {fileName}  →  {jsonPath}";

    private void WireSource(Control control, string fileName, string jsonPath)
    {
        control.Enter += (_, _) => ShowSource(fileName, jsonPath);
        control.Click += (_, _) => ShowSource(fileName, jsonPath);
    }

    private static TableLayoutPanel NewFieldGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            Padding = new Padding(12)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        return grid;
    }

    /// <param name="helpText">Plain-language explanation of this field plus a concrete
    /// example - shown in a popup when the circular "?" icon next to the field is
    /// clicked. Pass "" to skip the icon (rare - only for rows where a separate
    /// explanation doesn't add anything, e.g. a pure status readout).</param>
    /// <param name="stretchInput">False keeps the control at its own declared Width
    /// (anchored Left only) instead of stretching to fill the column - for fields
    /// whose content is inherently short (a date, an invoice number) where filling
    /// the whole form width just looks disproportionate. Defaults true - most fields
    /// (URLs, file paths, tokens) genuinely benefit from the extra width.</param>
    private void AddRow(TableLayoutPanel grid, string labelText, Control input, string fileName, string jsonPath, string helpText = "", bool stretchInput = true)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
        input.Margin = new Padding(3, 4, 3, 4);

        // Text/combo/date fields stretch to fill all available width in the
        // column (grows/shrinks with the form) - readability matters most for
        // these (URLs, file paths, tokens). Numeric spinners and checkboxes
        // stay their natural compact size - stretching a NumericUpDown or
        // CheckBox wide doesn't help readability, just looks broken.
        if (stretchInput && input is not (NumericUpDown or CheckBox))
        {
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }
        else
        {
            input.Anchor = AnchorStyles.Left;
        }

        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(input, 1, row);
        if (!string.IsNullOrEmpty(helpText))
        {
            grid.Controls.Add(CreateHelpIcon(labelText.Replace("\n", " "), helpText), 2, row);
        }
        WireSource(input, fileName, jsonPath);
    }

    // ---------------------------------------------------------------------
    // Per-field help: a small circular "?" badge next to each field that pops
    // up a plain-language explanation with a worked example on click.
    // ---------------------------------------------------------------------

    private static readonly Color HelpIconColor = Color.FromArgb(41, 128, 185);
    private static readonly Color HelpIconHoverColor = Color.FromArgb(52, 152, 219);

    private Button CreateHelpIcon(string title, string helpText)
    {
        const int size = 20;
        var icon = new Button
        {
            Text = "?",
            Width = size,
            Height = size,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(6, 4, 3, 3),
            FlatStyle = FlatStyle.Flat,
            BackColor = HelpIconColor,
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 8.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TabStop = false,
            UseVisualStyleBackColor = false
        };
        icon.FlatAppearance.BorderSize = 0;
        icon.FlatAppearance.MouseOverBackColor = HelpIconHoverColor;
        icon.FlatAppearance.MouseDownBackColor = HelpIconColor;

        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddEllipse(0, 0, size, size);
        icon.Region = new Region(path);

        icon.Click += (_, _) => ShowHelpPopup(title, helpText);
        return icon;
    }

    private void ShowHelpPopup(string title, string helpText)
    {
        using var popup = new Form
        {
            Text = "Field help",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            ClientSize = new Size(420, 260)
        };

        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 40,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            ForeColor = HelpIconColor,
            BackColor = Color.FromArgb(235, 245, 251),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 14, 0)
        };

        var body = new Label
        {
            Text = helpText,
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 14, 10),
            Font = new Font(Font.FontFamily, 9.5f)
        };

        var okButton = new Button
        {
            Text = "OK",
            Dock = DockStyle.Bottom,
            Height = 36,
            DialogResult = DialogResult.OK
        };

        popup.Controls.Add(body);
        popup.Controls.Add(titleLabel);
        popup.Controls.Add(okButton);
        popup.AcceptButton = okButton;
        popup.ShowDialog(this);
    }
}
