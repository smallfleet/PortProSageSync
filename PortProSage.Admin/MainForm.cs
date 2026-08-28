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
    public const string AppVersion = "2.16.0";

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

    /// <summary>What kind of run _pendingRequestId is currently tracking - all
    /// three share the exact same spawn-process/poll-result.json mechanism
    /// (StartManualRun, StartCustomerRefreshScan, StartCustomerRefreshExecute),
    /// so ResultPollTimer_Tick (MainForm.HistoryTab.cs) reads this once the
    /// result is final to decide what to do with it: show the ordinary
    /// invoice-worded completion pop-up, populate the Customer Refresh grid from
    /// the scan's candidates, or show the customer-worded completion pop-up.</summary>
    private enum PendingRunKind { None, ManualRun, CustomerRefreshScan, CustomerRefreshExecute }
    private PendingRunKind _pendingRunKind = PendingRunKind.None;

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
        _tabs.TabPages.Add(BuildCustomerRefreshTab()); // "Customer Refresh" - scan/select/run, on-demand customer create+update
        // Watermark tab removed 2026-08-25 - its single editable field now lives
        // at the top of "Automatic Sync" (MainForm.SyncTab.cs), the only tab that
        // actually consumes it.
        _tabs.TabPages.Add(BuildResultsTab());
        _tabs.TabPages.Add(BuildPortProTab());
        _tabs.TabPages.Add(BuildSage50Tab());
        _tabs.TabPages.Add(BuildSettingsTab()); // "Settings" - Email + Folder Locations
        _tabs.TabPages.Add(BuildLicensingAboutTab()); // Last tab - company/contact/license info; replaces the old plain About tab

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

        // Unconditional, every tab - not just History & Logs above - confirmed
        // live 2026-08-24: the global "Target Sage50" banner (and, if it detects
        // a genuine change, Customer Refresh's/History's own path dropdowns - see
        // RefreshGlobalTargetSage50Label) should never be more than one tab-click
        // stale relative to whatever's actually configured, regardless of which
        // tab the operator happens to land on.
        _tabs.SelectedIndexChanged += (_, _) => RefreshGlobalTargetSage50Label();

        Load += (_, _) => TryLoadConfig();
    }

    // ---------------------------------------------------------------------
    // Service folder selection + config load
    // ---------------------------------------------------------------------

    /// <summary>Global, always-visible regardless of which tab is active -
    /// requested explicitly 2026-08-24 so it's never ambiguous which real Sage 50
    /// company file is currently configured to be read from/written to, without
    /// having to go find it on the Sage 50 tab. Previously lived only on the
    /// Customer Refresh tab; moved here since it matters for every tab that can
    /// touch Sage 50, not just that one.</summary>
    private readonly Label _globalTargetSage50Label = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(150, 20, 20)
    };

    // Tracks whatever path this label showed the LAST time it was refreshed, so a
    // genuine change (a real Save on the Sage 50 tab picking a different path) can
    // be told apart from just refreshing again with nothing actually different -
    // see RefreshGlobalTargetSage50Label's own doc comment for why that distinction
    // matters. Null before the very first refresh - deliberately NOT treated as "a
    // change" on that first call, since there's nothing for the other tabs'
    // dropdowns to have gone stale relative to yet.
    private string? _lastKnownTargetSage50Path;

    /// <summary>Confirmed live 2026-08-24 this needs to run after every Sage 50 tab
    /// Save (and, since Test Connection now saves first too, after every Test
    /// Connection) - not just on a full config reload, which is the only thing
    /// that used to trigger it. Without this, the header could show a stale path
    /// indefinitely after actually switching to a different one, exactly the
    /// "banner says E:, Sage 50 tab says C:" mismatch that prompted this fix.
    /// Also resets Customer Refresh's and History &amp; Logs' own path-picker
    /// selections to follow the new path when it's genuinely changed (not just
    /// re-shown) - their own "never override an existing selection" rule is meant
    /// to protect a deliberate historical-path browse, not to permanently ignore
    /// the operator actually switching which company file is active.</summary>
    private void RefreshGlobalTargetSage50Label()
    {
        var path = _localSettings?.GetString("PortProSage.Sage50.CompanyDataPath")
            ?? _appSettings?.GetString("PortProSage.Sage50.CompanyDataPath");

        _globalTargetSage50Label.Text = string.IsNullOrWhiteSpace(path)
            ? "Target Sage50: (no path specified yet - set it on the Sage 50 tab)"
            : $"Target Sage50: {path}";

        var changed = _lastKnownTargetSage50Path is not null &&
                      !string.Equals(path, _lastKnownTargetSage50Path, StringComparison.OrdinalIgnoreCase);
        _lastKnownTargetSage50Path = path;

        if (changed && !string.IsNullOrWhiteSpace(path))
        {
            RefreshCustomerRefreshPathDropdown(forceFollowCurrent: true);
            RefreshHistoryPathDropdown(forceFollowCurrent: true);
        }
    }

    private Panel BuildServiceFolderBar()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 90 };

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
            "Opens USER_GUIDE.html - the full walkthrough of every tab and field in this app, with worked examples " +
            "and step-by-step recipes for common tasks - in your default web browser.\n\n" +
            "Looked for next to this app, next to the configured Service folder, and at the repo root on this " +
            "development machine - if none of those have it, you'll see a message saying so instead of it just " +
            "silently doing nothing.\n\n" +
            "Looking for the technical/developer documentation instead? That's README.md, in the same folder - " +
            "the User Guide itself links to it.";
        var readme = new Button { Text = "Help", Width = readmeWidth, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        readme.Click += (_, _) => OpenUserGuide();
        var readmeHelp = CreateHelpIcon("Help", readmeHelpText);
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

        // Third row: the global Target Sage50 banner - see its own doc comment.
        _globalTargetSage50Label.Font = new Font(Font, FontStyle.Bold);
        _globalTargetSage50Label.Location = new Point(8, 68);
        RefreshGlobalTargetSage50Label();
        RefreshAllTabsFromConfig += RefreshGlobalTargetSage50Label;

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
        panel.Controls.Add(_globalTargetSage50Label);
        return panel;
    }

    /// <summary>Finds USER_GUIDE.html next to this app, next to the configured
    /// Service folder (both the folder itself and walking up to a typical dev repo
    /// root), or at the hardcoded dev-machine repo path - covers both a normal dev
    /// checkout and a production install, as long as Install-Production.ps1 copied
    /// the docs alongside the published Service (see DEPLOYMENT.md). The .html
    /// version (not .md) is what the Help button opens - it's a self-contained,
    /// branded page meant to open directly in a browser, not whatever arbitrary
    /// app Windows happens to associate with .md files. USER_GUIDE.md still exists
    /// alongside it as the plain-text source/reference.</summary>
    private void OpenUserGuide()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "USER_GUIDE.html") };

        var serviceFolder = _serviceFolderBox.Text;
        if (!string.IsNullOrWhiteSpace(serviceFolder))
        {
            candidates.Add(Path.Combine(serviceFolder, "USER_GUIDE.html"));
            candidates.Add(Path.Combine(serviceFolder, "..", "USER_GUIDE.html"));
            candidates.Add(Path.Combine(serviceFolder, "..", "..", "..", "..", "USER_GUIDE.html")); // typical dev ...\PortProSage.Service\bin\Debug\net48 depth -> repo root
        }

        candidates.Add(@"C:\PortProSageSync\USER_GUIDE.html");

        var found = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (found is null)
        {
            MessageBox.Show(this,
                "Could not find USER_GUIDE.html in any of the usual locations (next to this app, next to the " +
                "Service folder, or the repo root).",
                "User Guide not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    /// <summary>Every distinct Company data path this operator has ever saved on
    /// the Sage 50 tab (MainForm.Sage50Tab.cs), most-recently-saved first, capped
    /// at 10 - backs that field's editable-dropdown history. Deliberately a
    /// separate Admin-UI-only list, not derived from state.db's "known paths"
    /// (Sage50PathStateService, used by the Customer Refresh/History & Logs
    /// dropdowns) - that list only grows once a real sync has actually run
    /// against a path, so a path just typed/saved here but never yet connected to
    /// wouldn't show up in its own history otherwise.</summary>
    private static List<string> LoadPreviousSage50Paths()
    {
        try
        {
            return (LoadAdminSettings()["PreviousSage50Paths"] as JsonArray)?
                .Select(n => n?.GetValue<string>() ?? "")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList() ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static void RecordSage50Path(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        SaveAdminSettings(json =>
        {
            var existing = (json["PreviousSage50Paths"] as JsonArray)?
                .Select(n => n?.GetValue<string>() ?? "")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList() ?? new List<string>();

            existing.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            existing.Insert(0, path);
            if (existing.Count > 10) existing = existing.Take(10).ToList();

            json["PreviousSage50Paths"] = new JsonArray(existing.Select(p => (JsonNode)p).ToArray());
        });
    }

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
                // Unchecked (no cutoff saved yet) previously left the picker's
                // underlying Value at DateTimePicker's own default (today), so
                // simply checking the box for the first time started from
                // "today" instead of a sensible starting point - confirmed live
                // 2026-08-25 the operator wants it to start 6 months back instead,
                // both on first-ever load and any time the cutoff gets cleared.
                var sixMonthsBack = DateTime.Today.AddMonths(-6);
                _syncCutoffInvoiceDate.Checked = false;
                _syncCutoffInvoiceDate.Value = sixMonthsBack;
                _runCutoffInvoiceDate.Checked = false;
                _runCutoffInvoiceDate.Value = sixMonthsBack;
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
    // Dry run - one setting (PortProSage:Sage50:DryRun), shown and editable on
    // BOTH the Manual Run tab and the Sage 50 tab, same shared/immediate-save
    // pattern as Show command window above. Confirmed live 2026-08-22: this used
    // to be a read-only status label on Manual Run (mirroring whatever the Sage 50
    // tab's checkbox said), which meant testing/toggling Dry Run for one run
    // required navigating to the Sage 50 tab and clicking its own Save button -
    // now it's the same live setting, editable from either place, in effect
    // immediately either way (same as Cutoff Invoice Date/Show command window).
    // ---------------------------------------------------------------------

    private const string RunDryRunHelpText =
        "When checked, this run only SIMULATES writes - nothing is actually created in Sage 50, the log just " +
        "says what it would have done instead. This is the exact same setting as \"Dry run\" on the Sage 50 tab " +
        "- checking or unchecking it here changes it there too (and vice versa), saved immediately either way, " +
        "no separate Save button needed for this one field.\n\n" +
        "Always test something unfamiliar (a new date range, an account mapping change) with this checked first, " +
        "confirm the log/History & Logs looks right, then uncheck it for the real run.\n\n" +
        "A Dry Run invoice is never marked as imported, so the exact same range run again for real afterward " +
        "will genuinely process it, not skip it as already done.\n\n" +
        "Checking this also sets \"Max invoices to process\" to 10 automatically - a full-range Dry Run isn't " +
        "usually necessary just to sanity-check behavior, so this keeps a test run quick by default. Unchecking " +
        "Dry Run resets Max invoices back to 0 (no limit), so a leftover test cap can't silently limit a real " +
        "run. Change Max invoices by hand afterward if you need a different number for this particular test.";

    private void WireDryRunControl(CheckBox box)
    {
        box.CheckedChanged += (_, _) => SaveDryRun(box);
    }

    private void RefreshDryRunControls()
    {
        if (_appSettings is null) return;

        _suppressDryRunEvents = true;
        try
        {
            var dryRun = _appSettings.GetBool("PortProSage.Sage50.DryRun");
            _runDryRun.Checked = dryRun;
            _sage50DryRun.Checked = dryRun;
        }
        finally
        {
            _suppressDryRunEvents = false;
        }
    }

    private void SaveDryRun(CheckBox source)
    {
        if (_appSettings is null || _suppressDryRunEvents) return;

        _appSettings.SetBool("PortProSage.Sage50.DryRun", source.Checked);
        _appSettings.Save();
        RefreshDryRunControls();

        // Confirmed live 2026-08-22: requested so a Dry Run defaults to a quick,
        // bounded test (10 invoices) rather than silently simulating an entire
        // range, and so turning Dry Run back off can't leave a forgotten test cap
        // limiting a real run. Applies regardless of which of the two checkboxes
        // (this one, or the Sage 50 tab's) was actually toggled - Max invoices is
        // a Manual Run-only field, but the safety default matters either way.
        _runMaxInvoices.Value = source.Checked ? Math.Min(10, _runMaxInvoices.Maximum) : 0;
    }

    private bool _suppressDryRunEvents;

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

    /// <summary>Adds a bold heading + gray subtext row pair - used by the Settings
    /// tab's sub-sections and (previously) the Watermark tab. Moved here from
    /// MainForm.WatermarkTab.cs 2026-08-25 when that tab was removed, since
    /// SettingsTab.cs still needs it.</summary>
    private static void AddSectionHeading(TableLayoutPanel grid, string heading, string subText = "")
    {
        var headingRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var headingLabel = new Label
        {
            Text = heading,
            AutoSize = true,
            Font = new Font(grid.Font, FontStyle.Bold),
            Margin = new Padding(3, 8, 3, 2)
        };
        grid.Controls.Add(headingLabel, 0, headingRow);
        grid.SetColumnSpan(headingLabel, 3);

        if (string.IsNullOrEmpty(subText)) return;

        var subRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var subLabel = new Label
        {
            Text = subText,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 4),
            MaximumSize = new Size(700, 0)
        };
        grid.Controls.Add(subLabel, 0, subRow);
        grid.SetColumnSpan(subLabel, 3);
    }

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
        const int width = 460;
        const int bodyPadding = 14;
        const int titleHeight = 40;
        const int okHeight = 36;

        var bodyFont = new Font(Font.FontFamily, 9.5f);

        // Fixed 260px height (and no scrolling) silently clipped any help text
        // longer than a few short lines with no way to read the rest - confirmed
        // live 2026-08-25 on the account-mismatch checkbox's longer, example-based
        // text. Now sized to the actual content: measure the wrapped text at this
        // popup's real width, then grow the form to fit (capped so it can never
        // exceed the screen), with AutoScroll as a safety net for anything still
        // too long for even that cap.
        var bodyWidth = width - bodyPadding * 2;
        var measured = TextRenderer.MeasureText(helpText, bodyFont, new Size(bodyWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

        var maxHeight = (int)(Screen.FromControl(this).WorkingArea.Height * 0.85);
        var wantedHeight = titleHeight + measured.Height + bodyPadding * 2 + okHeight;
        var clientHeight = Math.Clamp(wantedHeight, 260, maxHeight);

        using var popup = new Form
        {
            Text = "Field help",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            ShowInTaskbar = false,
            ClientSize = new Size(width, clientHeight)
        };

        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = titleHeight,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            ForeColor = HelpIconColor,
            BackColor = Color.FromArgb(235, 245, 251),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(bodyPadding, 0, bodyPadding, 0)
        };

        var bodyScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };

        var body = new Label
        {
            Text = helpText,
            AutoSize = true,
            MaximumSize = new Size(bodyWidth, 0),
            Padding = new Padding(bodyPadding, 10, bodyPadding, 10),
            Font = bodyFont
        };
        bodyScroll.Controls.Add(body);

        var okButton = new Button
        {
            Text = "OK",
            Dock = DockStyle.Bottom,
            Height = okHeight,
            DialogResult = DialogResult.OK
        };

        popup.Controls.Add(bodyScroll);
        popup.Controls.Add(titleLabel);
        popup.Controls.Add(okButton);
        popup.AcceptButton = okButton;
        popup.ShowDialog(this);
    }
}
