using System.Text.Json;
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
    /// <summary>Shown on the About tab so it's obvious at a glance whether a
    /// freshly-built/installed exe actually is the one just built - confirmed live
    /// 2026-08-12 there was no way to tell apart an old, already-running Admin.exe
    /// from a newly rebuilt one just by looking at the UI.
    ///
    /// X.YY.ZZ, bumped by hand with every build handed out for testing/release:
    ///   X  (major)  - a big feature release.
    ///   YY (minor)  - a "major release" - a meaningful, release-worthy batch of
    ///                 changes (e.g. what ships in the next production installer).
    ///   ZZ (build)  - any other new exe, including small dev-test iterations.
    /// </summary>
    public const string AppVersion = "2.01.16";

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
        Text = $"PortProSage Admin - v{AppVersion}";
        Width = 1000;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        var statusStrip = new StatusStrip { Dock = DockStyle.Bottom };
        statusStrip.Items.Add(_sourceLabel);

        var topBar = BuildServiceFolderBar();

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildRunTab()); // "Manual Run"
        _tabs.TabPages.Add(BuildSyncTab()); // "Automatic Sync" - includes the Start/Stop Automatic Service controls
        _tabs.TabPages.Add(BuildWatermarkTab());
        _tabs.TabPages.Add(BuildResultsTab());
        _tabs.TabPages.Add(BuildPortProTab());
        _tabs.TabPages.Add(BuildSage50Tab());
        _tabs.TabPages.Add(BuildSettingsTab()); // "Settings" - Email + Folder Locations
        _tabs.TabPages.Add(BuildAboutTab()); // Last tab - company/contact info

        Controls.Add(_tabs);
        Controls.Add(topBar);
        Controls.Add(statusStrip);

        _resultPollTimer.Tick += ResultPollTimer_Tick;

        // Landing on History & Logs (by clicking it, or programmatically via
        // SelectHistoryTab when a run starts) always shows the latest activity
        // first - refreshed from disk, then the newest (top) row selected -
        // rather than leaving whatever was selected the last time this tab was
        // open. Confirmed live 2026-08-12 this was the expected default; picking
        // an older run to inspect is still just a click away, this only changes
        // what's shown automatically on arrival.
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab?.Text.StartsWith("History", StringComparison.Ordinal) == true)
            {
                RefreshHistoryList();
                SelectTopHistoryRow();
            }
        };

        Load += (_, _) => TryLoadConfig();
    }

    // ---------------------------------------------------------------------
    // Service folder selection + config load
    // ---------------------------------------------------------------------

    private Panel BuildServiceFolderBar()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 64 };

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

        // Right-aligned (Anchor, not a fixed coordinate like the buttons above) so
        // it stays pinned to the right edge if the window is ever resized wider,
        // rather than leaving a growing gap.
        const int readmeWidth = 70;
        const string readmeHelpText =
            "Opens README.md - the technical overview of how this whole system works (automatic sync, manual " +
            "triggers, validation/auto-matching rules, log/troubleshooting locations) - in whatever application " +
            "is associated with .md files on this computer (Notepad, VS Code, a browser, etc).\n\n" +
            "Looked for next to this app, next to the configured Service folder, and at the repo root on this " +
            "development machine - if none of those have it, you'll see a message saying so instead of it just " +
            "silently doing nothing.";
        var readme = new Button { Text = "Readme", Width = readmeWidth, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        readme.Click += (_, _) => OpenReadme();
        var readmeHelp = CreateHelpIcon("Readme", readmeHelpText);
        readmeHelp.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        // Directly visible in the always-on-screen top bar, not just the window
        // title bar (easy to miss/obscured when maximized) or the About tab
        // (requires navigating there) - confirmed live 2026-08-12 there was no
        // quick way to tell a freshly rebuilt exe apart from an old one still
        // running, which cost real time chasing "changes aren't showing up".
        var versionLabel = new Label
        {
            Text = $"v{AppVersion}",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        panel.SizeChanged += (_, _) =>
        {
            readmeHelp.Location = new Point(panel.Width - readmeHelp.Width - 8, 8);
            readme.Location = new Point(readmeHelp.Left - readme.Width - 6, 5);
            versionLabel.Location = new Point(panel.Width - versionLabel.Width - 8, 42);
        };
        readmeHelp.Location = new Point(panel.Width - readmeHelp.Width - 8, 8);
        readme.Location = new Point(readmeHelp.Left - readme.Width - 6, 5);
        versionLabel.Location = new Point(panel.Width - versionLabel.Width - 8, 42);

        // Second row: always-visible process status + a Stop button that works
        // regardless of which tab (Manual Run / Automatic Sync) actually owns
        // whatever is running - see MainForm.ServiceControl.cs's
        // RefreshServiceStatus/StopWhicheverIsRunning.
        var statusLabelCaption = new Label { Text = "Process:", AutoSize = true, Location = new Point(8, 40) };
        _headerStatusLabel.Font = new Font(_headerStatusLabel.Font, FontStyle.Bold);
        _headerStatusLabel.Location = new Point(100, 40);
        _headerActivityIndicator.Location = new Point(460, 39);
        _headerStopButton.Location = new Point(590, 36);
        _headerStopButton.Click += (_, _) => StopWhicheverIsRunning();

        panel.Controls.Add(label);
        panel.Controls.Add(_serviceFolderBox);
        panel.Controls.Add(browse);
        panel.Controls.Add(reload);
        panel.Controls.Add(readme);
        panel.Controls.Add(readmeHelp);
        panel.Controls.Add(statusLabelCaption);
        panel.Controls.Add(_headerStatusLabel);
        panel.Controls.Add(_headerActivityIndicator);
        panel.Controls.Add(_headerStopButton);
        panel.Controls.Add(versionLabel);
        return panel;
    }

    /// <summary>Finds README.md next to this app, next to the configured Service
    /// folder (both the folder itself and walking up to a typical dev repo root),
    /// or at the hardcoded dev-machine repo path - covers both a normal dev
    /// checkout and a production install, as long as Install-Production.ps1 copied
    /// the docs alongside the published Service (see DEPLOYMENT.md).</summary>
    private void OpenReadme()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "README.md") };

        var serviceFolder = _serviceFolderBox.Text;
        if (!string.IsNullOrWhiteSpace(serviceFolder))
        {
            candidates.Add(Path.Combine(serviceFolder, "README.md"));
            candidates.Add(Path.Combine(serviceFolder, "..", "README.md"));
            candidates.Add(Path.Combine(serviceFolder, "..", "..", "..", "..", "README.md")); // typical dev ...\PortProSage.Service\bin\Debug\net48 depth -> repo root
        }

        candidates.Add(@"C:\PortProSageSync\README.md");

        var found = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (found is null)
        {
            MessageBox.Show(this,
                "Could not find README.md in any of the usual locations (next to this app, next to the Service " +
                "folder, or the repo root).",
                "Readme not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = found,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open:\n{found}\n\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Checks the documented production publish target first (README "Build &amp;
    /// run"), then this dev machine's known build output - either way, the user
    /// can override and it's just a starting guess, not a hard assumption.
    /// </summary>
    // Remembers the last folder a config was actually loaded from, so restarting
    // the app doesn't fall back to guessing every time - confirmed live 2026-08-11
    // this was a real, repeated annoyance in production: the box isn't a .NET
    // Settings-bound control, so with nothing persisted here it re-guessed (and
    // got it wrong) on every single launch even after a successful manual fix.
    private static string AdminSettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PortProSageAdmin", "admin-settings.json");

    /// <summary>Reads the whole local admin-settings.json as a JsonObject - every
    /// feature that persists small UI state here (Service folder, Manual Run's
    /// last-used fields, ...) reads/writes through this and SaveAdminSettings
    /// rather than writing the file directly, so one feature's save never clobbers
    /// another's already-saved keys.</summary>
    private static JsonObject LoadAdminSettings()
    {
        try
        {
            if (!File.Exists(AdminSettingsFilePath)) return new JsonObject();
            // FileShare.ReadWrite, not File.ReadAllText's default (FileShare.Read) - reads
            // should never lock out a writer (same principle applied throughout this fix -
            // see TriggerService.TryReadResult/RunHistoryService.TryDeserialize).
            using var stream = new FileStream(AdminSettingsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject(); // corrupt/unreadable settings file - start fresh rather than crash
        }
    }

    private static void SaveAdminSettings(Action<JsonObject> mutate)
    {
        try
        {
            var json = LoadAdminSettings();
            mutate(json);
            Directory.CreateDirectory(Path.GetDirectoryName(AdminSettingsFilePath)!);
            File.WriteAllText(AdminSettingsFilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort only - a failed settings write should never block using the app.
        }
    }

    private static string? LoadSavedServiceFolder()
    {
        try { return LoadAdminSettings()["ServiceFolder"]?.GetValue<string>(); }
        catch { return null; }
    }

    private static void SaveServiceFolder(string folder) => SaveAdminSettings(json => json["ServiceFolder"] = folder);

    private static string GuessServiceFolder()
    {
        const string defaultInstalledFolder = @"C:\PortProSageSync\Service"; // Install-Production.ps1 / PortProSageSyncInstaller.exe layout

        var candidates = new List<string>();
        var saved = LoadSavedServiceFolder();
        if (!string.IsNullOrWhiteSpace(saved)) candidates.Add(saved);
        candidates.Add(defaultInstalledFolder);
        candidates.Add(@"C:\PortProSageSync\PortProSage.Service\bin\Debug\net48");
        candidates.Add(@"C:\PortProSageSync\PortProSage.Service\bin\Release\net48");
        candidates.Add(@"C:\PortProSageSync\bin"); // legacy manual-install layout, superseded by install-service.ps1's removal

        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, AppSettingsFileName))) ?? defaultInstalledFolder;
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
        SaveServiceFolder(folder);

        RefreshAllTabsFromConfig?.Invoke();
    }

    /// <summary>Wired by each Build*Tab() method to repopulate its controls after a (re)load.</summary>
    private Action? RefreshAllTabsFromConfig;

    // ---------------------------------------------------------------------
    // Cutoff Invoice Date - one setting (SyncSettings.CutoffInvoiceDate),
    // shown and editable on BOTH the Automatic Sync tab and the Manual Run tab
    // (see MainForm.SyncTab.cs / MainForm.RunTab.cs for where each is laid out).
    // Unlike every other field in this app, this one saves immediately on
    // change rather than waiting for a tab's own Save button - it needs to
    // stay in sync between two tabs the instant either one changes it, and a
    // date-with-cutoff toggle is closer in spirit to the Automatic/Manual Run
    // controls (an immediate action) than to a batch of fields you edit and
    // then commit together.
    // ---------------------------------------------------------------------

    private DateTimePicker _syncCutoffInvoiceDate = new() { ShowCheckBox = true, Checked = false, Width = 220 };
    private DateTimePicker _runCutoffInvoiceDate = new() { ShowCheckBox = true, Checked = false, Width = 220 };
    private bool _suppressCutoffInvoiceDateEvents;

    private const string CutoffInvoiceDateHelpText =
        "The LOWER bound - if set, no invoice dated BEFORE this fixed date is ever processed, by Manual Run OR the " +
        "Automatic Service, regardless of mode. (For the separate UPPER bound - holding back the most recent N " +
        "days - see \"Automatic Sync - Processing Delay (Days)\" on the Automatic Sync tab; that one's a rolling " +
        "number of days, this one's a fixed calendar date.)\n\n" +
        "This exists to catch, in our own code, a real failure that's happened more than once: Sage 50's own " +
        "\"Do Not Allow Transactions Dated Before\" company setting rejects a too-old invoice mid-write, which " +
        "terminates the whole sync process immediately. Setting this here stops it before that ever happens - the " +
        "invoice is just skipped and logged, nothing crashes.\n\n" +
        "Shared between the Automatic Sync tab and the Manual Run tab - changing it in either place updates both " +
        "immediately (saved right away, not on a tab's own Save button).\n\n" +
        "Uncheck the box to remove the cutoff entirely - every invoice is eligible regardless of date (the default).";

    private void WireCutoffInvoiceDateControl(DateTimePicker picker)
    {
        // DateTimePicker has no CheckedChanged event (a real WinForms gap) - Click
        // fires after the checkbox's internal state has already updated, so it
        // catches a checkbox toggle; ValueChanged catches an actual date edit.
        // SaveCutoffInvoiceDate just re-reads current Checked/Value either way, so
        // it doesn't matter which one fired or if both fire for the same change.
        picker.Click += (_, _) => SaveCutoffInvoiceDate(picker);
        picker.ValueChanged += (_, _) => SaveCutoffInvoiceDate(picker);
    }

    private void RefreshCutoffInvoiceDateControls()
    {
        if (_appSettings is null) return;

        _suppressCutoffInvoiceDateEvents = true;
        try
        {
            var raw = _appSettings.GetString("PortProSage.Sync.CutoffInvoiceDate", "");
            if (DateTimeOffset.TryParse(raw, out var date))
            {
                _syncCutoffInvoiceDate.Checked = true;
                _syncCutoffInvoiceDate.Value = date.LocalDateTime.Date;
                _runCutoffInvoiceDate.Checked = true;
                _runCutoffInvoiceDate.Value = date.LocalDateTime.Date;
            }
            else
            {
                _syncCutoffInvoiceDate.Checked = false;
                _runCutoffInvoiceDate.Checked = false;
            }
        }
        finally
        {
            _suppressCutoffInvoiceDateEvents = false;
        }
    }

    private void SaveCutoffInvoiceDate(DateTimePicker source)
    {
        if (_appSettings is null || _suppressCutoffInvoiceDateEvents) return;

        if (source.Checked)
        {
            _appSettings.SetString("PortProSage.Sync.CutoffInvoiceDate", source.Value.Date.ToString("yyyy-MM-dd"));
        }
        else
        {
            _appSettings.SetOptionalString("PortProSage.Sync.CutoffInvoiceDate", null);
        }
        _appSettings.Save();
        RefreshCutoffInvoiceDateControls();
    }

    // ---------------------------------------------------------------------
    // Show command window - one setting (PortProSage:Sync:ShowCommandWindow),
    // shown and editable on BOTH the Automatic Sync tab and the Manual Run tab,
    // same shared/immediate-save pattern as Cutoff Invoice Date above. Purely an
    // Admin-app launch preference (the Service itself has no notion of this -
    // it's decided by whoever starts the process), so it lives under Sync rather
    // than being read by SyncOrchestrator at all.
    // ---------------------------------------------------------------------

    private CheckBox _syncShowCommandWindow = new() { Text = "Show command window while running", AutoSize = true };
    private CheckBox _runShowCommandWindow = new() { Text = "Show command window while running", AutoSize = true };
    private bool _suppressShowCommandWindowEvents;

    private const string ShowCommandWindowHelpText =
        "Whether PortProSage.Service.exe's console window is shown while a Manual Run or the Automatic Service is " +
        "running. Checked (default) shows it, so you can watch progress live, same as before. Unchecked runs it " +
        "hidden in the background instead - useful once you trust it and don't want a window popping up every " +
        "time, but you'll only be able to watch progress via the History & Logs tab, not the console itself.\n\n" +
        "Applies to BOTH Manual Run and Start Automatic Service - shared between the Automatic Sync tab and the " +
        "Manual Run tab, saved immediately when changed on either.";

    private void WireShowCommandWindowControl(CheckBox box)
    {
        box.CheckedChanged += (_, _) => SaveShowCommandWindow(box);
    }

    private void RefreshShowCommandWindowControls()
    {
        if (_appSettings is null) return;

        _suppressShowCommandWindowEvents = true;
        try
        {
            var show = _appSettings.GetBool("PortProSage.Sync.ShowCommandWindow", true);
            _syncShowCommandWindow.Checked = show;
            _runShowCommandWindow.Checked = show;
        }
        finally
        {
            _suppressShowCommandWindowEvents = false;
        }
    }

    private void SaveShowCommandWindow(CheckBox source)
    {
        if (_appSettings is null || _suppressShowCommandWindowEvents) return;

        _appSettings.SetBool("PortProSage.Sync.ShowCommandWindow", source.Checked);
        _appSettings.Save();
        RefreshShowCommandWindowControls();
    }

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

    // Windows' own accent blue - distinct from the default gray button so a
    // primary action (Save, Refresh) actually catches the eye instead of
    // blending into the surrounding gray form.
    private static readonly Color ActionButtonColor = Color.FromArgb(0, 120, 215);

    /// <summary>Wraps a button as a fixed-size, left-aligned, colored control inside
    /// a thin docked bar - NOT a bare Dock=Top/Bottom button, which WinForms
    /// silently stretches to the full width of its container regardless of any
    /// Width set on it. Confirmed live 2026-08-11: several Save/Refresh buttons
    /// built that way ended up as a wide, plain-gray strip that was easy to miss
    /// entirely rather than read as a clickable button.</summary>
    private static Panel CreateActionButtonBar(Button button, DockStyle dock = DockStyle.Bottom, int barHeight = 42)
    {
        button.AutoSize = false;
        button.Width = Math.Max(button.Width, 160);
        button.Height = 30;
        button.Location = new Point(10, (barHeight - button.Height) / 2);
        button.BackColor = ActionButtonColor;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Cursor = Cursors.Hand;

        var bar = new Panel { Dock = dock, Height = barHeight };
        bar.Controls.Add(button);
        return bar;
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
        var willStretch = stretchInput && input is not (NumericUpDown or CheckBox);
        input.Anchor = willStretch ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;

        grid.Controls.Add(label, 0, row);

        if (willStretch)
        {
            // Content fills the whole column, so the help icon's fixed column-2
            // position already sits right next to where it visually ends.
            grid.Controls.Add(input, 1, row);
            if (!string.IsNullOrEmpty(helpText))
            {
                grid.Controls.Add(CreateHelpIcon(labelText.Replace("\n", " "), helpText), 2, row);
            }
        }
        else
        {
            // Content doesn't fill the column - column 2's fixed far-right position
            // would leave a big empty gap after it. Wrap the help icon together
            // with the content instead, so it sits immediately next to where the
            // content actually ends.
            AddWrappedWithHelp(grid, row, input, labelText, helpText);
        }

        WireSource(input, fileName, jsonPath);
    }

    /// <summary>Places `content` (already sized/anchored by the caller) alone in
    /// column 1 if there's no help text, or wrapped together with a help icon
    /// immediately following it if there is - used for any row whose content
    /// doesn't stretch to fill the column, so the icon lands right next to the
    /// content instead of stranded at the column's fixed far-right edge.</summary>
    private void AddWrappedWithHelp(TableLayoutPanel grid, int row, Control content, string labelText, string helpText)
    {
        if (string.IsNullOrEmpty(helpText))
        {
            grid.Controls.Add(content, 1, row);
            return;
        }

        var wrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        wrap.Controls.Add(content);
        wrap.Controls.Add(CreateHelpIcon(labelText.Replace("\n", " "), helpText));
        grid.Controls.Add(wrap, 1, row);
    }

    /// <summary>Like AddRow, but with an extra button (e.g. "Test Connection") next
    /// to the field instead of stretching it full-width - built by hand rather than
    /// routed through AddRow for the same reason as AddFolderRow (MainForm.SyncTab.cs):
    /// wiring WireSource to a wrapper panel instead of the actual input control would
    /// silently break "click to see source" for this field.</summary>
    private void AddRowWithButton(TableLayoutPanel grid, string labelText, Control input, string fileName, string jsonPath,
        string helpText, string buttonText, EventHandler onClick, int inputWidth = FieldHalfWidth)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = labelText, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };

        input.Width = inputWidth;
        input.Anchor = AnchorStyles.Left;
        input.Margin = new Padding(3, 4, 3, 4);

        var button = new Button { Text = buttonText, AutoSize = true, Height = 23, Margin = new Padding(6, 5, 3, 3) };
        button.Click += onClick;

        var wrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        wrap.Controls.Add(input);
        wrap.Controls.Add(button);
        if (!string.IsNullOrEmpty(helpText))
        {
            // Wrapped together with the input+button, not column 2's fixed
            // far-right position - so the icon sits right next to the button
            // instead of stranded past a large empty gap.
            wrap.Controls.Add(CreateHelpIcon(labelText.Replace("\n", " "), helpText));
        }

        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(wrap, 1, row);
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
