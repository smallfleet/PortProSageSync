using System.Diagnostics;
using System.Management;

namespace PortProSage.Admin;

public enum ServiceRunState { NotRunning, AutomaticRunning, ManualRunning }

/// <summary>
/// Lets the operator start/stop the long-running automatic pipeline
/// (PortProSage.Service.exe with no arguments - polling + trigger-folder
/// watching) directly from the Run tab. Distinguishes "automatic" from a
/// "manual run" (--run-once, see MainForm.RunTab.cs) by inspecting each
/// matching process's real command line via WMI - not just by whether this
/// app happened to be the one that started it, since either kind can also be
/// started outside this app entirely (double-clicking the exe, or as a
/// Windows Service). Automatic and manual are mutually exclusive by design
/// (see MainForm.RunTab.cs) because both would connect to Sage 50 under the
/// same configured username, which Sage 50 rejects for a second simultaneous
/// session.
/// </summary>
public partial class MainForm
{
    private readonly Label _serviceStatusLabel = new() { AutoSize = true };
    private readonly Button _startServiceButton = new() { Text = "Start Automatic Service", Width = 160 };
    private readonly Button _stopServiceButton = new() { Text = "Stop Automatic Service", Width = 160 };
    private readonly System.Windows.Forms.Timer _serviceStatusTimer = new() { Interval = 3000 };

    private const string AutomaticServiceHelpText =
        "Starts/stops the long-running automatic pipeline (PortProSage.Service.exe with no extra arguments) - " +
        "the same thing that happens if you double-click the exe yourself. Once running, it does two things " +
        "continuously: watches the Trigger folder for manual requests every 15 seconds, and polls PortPro for " +
        "newly changed invoices every Polling interval minutes (see Sync tab), using whatever is currently saved " +
        "in appsettings.json / appsettings.Local.json.\n\n" +
        "Disabled while a Manual Run is in progress, and Manual Run is disabled while this is running - both would " +
        "otherwise try to open Sage 50 under the same configured username at the same time, which Sage 50 rejects.";

    private Panel BuildServiceControlPanel()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 46 };

        _startServiceButton.Location = new Point(0, 8);
        _stopServiceButton.Location = new Point(170, 8);
        var help = CreateHelpIcon("Automatic Service", AutomaticServiceHelpText);
        help.Location = new Point(340, 12);
        _serviceStatusLabel.Location = new Point(375, 15);
        _serviceStatusLabel.Font = new Font(_serviceStatusLabel.Font, FontStyle.Bold);

        _startServiceButton.Click += (_, _) => StartServiceProcess();
        _stopServiceButton.Click += (_, _) => StopServiceProcess();

        panel.Controls.Add(_startServiceButton);
        panel.Controls.Add(_stopServiceButton);
        panel.Controls.Add(help);
        panel.Controls.Add(_serviceStatusLabel);

        _serviceStatusTimer.Tick += (_, _) => RefreshServiceStatus();
        _serviceStatusTimer.Start();
        RefreshServiceStatus();

        return panel;
    }

    private string ServiceExePath => Path.Combine(_serviceFolderBox.Text, "PortProSage.Service.exe");

    /// <summary>All live processes actually running PortProSage.Service.exe from the
    /// configured Service folder - both automatic and manual instances, and
    /// regardless of whether this app started them.</summary>
    private List<Process> FindMatchingServiceProcesses()
    {
        var exePath = ServiceExePath;
        var matches = new List<Process>();
        foreach (var p in Process.GetProcessesByName("PortProSage.Service"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(p);
                }
            }
            catch
            {
                // Access denied reading another user's process module info, or it
                // exited between enumeration and inspection - not a match.
            }
        }
        return matches;
    }

    private static string? GetCommandLine(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (var obj in searcher.Get())
            {
                return obj["CommandLine"] as string;
            }
        }
        catch
        {
            // WMI can fail for a process that's already exited, or under restricted
            // permissions - treat as "unknown", not fatal.
        }
        return null;
    }

    /// <summary>Current run state plus the specific process for whichever kind is
    /// active (null if NotRunning). If both somehow exist at once (started outside
    /// this app, bypassing the mutual-exclusion buttons), Manual takes priority to
    /// report - it's the more time-sensitive one to know about.</summary>
    private (ServiceRunState State, Process? Process) GetServiceRunState()
    {
        Process? automatic = null;
        foreach (var p in FindMatchingServiceProcesses())
        {
            var commandLine = GetCommandLine(p.Id) ?? "";
            if (commandLine.Contains("--run-once", StringComparison.OrdinalIgnoreCase))
            {
                return (ServiceRunState.ManualRunning, p);
            }
            automatic ??= p;
        }
        return automatic is not null ? (ServiceRunState.AutomaticRunning, automatic) : (ServiceRunState.NotRunning, null);
    }

    private void RefreshServiceStatus()
    {
        var (state, process) = GetServiceRunState();

        switch (state)
        {
            case ServiceRunState.AutomaticRunning:
                _serviceStatusLabel.Text = $"AUTOMATIC RUNNING (PID {process!.Id})";
                _serviceStatusLabel.ForeColor = Color.DarkGreen;
                _startServiceButton.Enabled = false;
                _stopServiceButton.Enabled = true;
                break;
            case ServiceRunState.ManualRunning:
                _serviceStatusLabel.Text = $"MANUAL RUNNING (PID {process!.Id})";
                _serviceStatusLabel.ForeColor = Color.DarkOrange;
                _startServiceButton.Enabled = false;
                _stopServiceButton.Enabled = false; // this button only controls the automatic instance
                break;
            default:
                _serviceStatusLabel.Text = "NOT RUNNING";
                _serviceStatusLabel.ForeColor = Color.DarkRed;
                _startServiceButton.Enabled = true;
                _stopServiceButton.Enabled = false;
                break;
        }

        UpdateManualRunButtonStates(state, process);
    }

    private void StartServiceProcess()
    {
        var exePath = ServiceExePath;
        if (!File.Exists(exePath))
        {
            MessageBox.Show(this, $"Could not find PortProSage.Service.exe in:\n{_serviceFolderBox.Text}", "Not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (state, _) = GetServiceRunState();
        if (state != ServiceRunState.NotRunning)
        {
            MessageBox.Show(this, "Something is already running (automatic or manual) - see the status above.",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(this, BuildAutomaticStartConfirmationText(exePath),
            "Confirm start - Automatic Service", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = _serviceFolderBox.Text,
            UseShellExecute = true // opens its own console window, so the operator can watch it run live
        });

        RefreshServiceStatus();
    }

    /// <summary>The actual resolved values that govern the automatic pipeline once
    /// started - not just "reads appsettings.json", since that alone doesn't tell
    /// the operator what will actually happen without opening the file separately.</summary>
    private string BuildAutomaticStartConfirmationText(string exePath)
    {
        var lines = new List<string>
        {
            "Start the automatic Service now?",
            "",
            $"Write mode: {(_sage50DryRun.Checked ? "DRY RUN (simulated - nothing written to Sage 50)" : "REAL WRITE (changes Sage 50 for real)")}",
            $"Sage 50 company file: {_sage50CompanyDataPath.Text}",
            $"Sage 50 username: {_sage50UserName.Text}",
            $"PortPro base URL: {_portProBaseUrl.Text}",
            $"Polling interval: every {_syncPollingIntervalMinutes.Value} minute(s)",
            $"Trigger folder watched: {_triggerFolder}",
            $"Auto-create customers: {_sage50AutoCreateCustomers.Checked}   Auto-create items: {_sage50AutoCreateItems.Checked}",
            "",
            $"(from {AppSettingsFileName} / {LocalSettingsFileName} in {_serviceFolderBox.Text})",
            "",
            $"Executable: {exePath}"
        };
        return string.Join(Environment.NewLine, lines);
    }

    private void StopServiceProcess()
    {
        var (state, process) = GetServiceRunState();
        if (state != ServiceRunState.AutomaticRunning || process is null) { RefreshServiceStatus(); return; }

        var confirm = MessageBox.Show(this,
            $"Stop the automatic Service (PID {process.Id}) now?\n\n" +
            "This interrupts automatic polling and manual-trigger processing until it's started again.",
            "Confirm stop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        GracefulStop(process);
        RefreshServiceStatus();
    }

    /// <summary>Shared by both the automatic Stop button and Manual Run's own Stop
    /// button - CloseMainWindow sends the console-close signal .NET's Generic Host
    /// handles as a graceful-shutdown request (same as Ctrl+C), which is what lets
    /// the host disposal / CloseDatabase() fix run properly rather than an abrupt
    /// Kill(). Falls back to Kill() only if it doesn't respond in time.</summary>
    private static void GracefulStop(Process process)
    {
        try
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
            }
        }
        catch
        {
            // best effort - if it already exited or access is denied, there's
            // nothing more to do here.
        }
    }

    private void TestSage50Connection() => RunConnectionTest("sage50", "Sage 50 connection test");

    private void TestPortProConnection() => RunConnectionTest("portpro", "PortPro connection test");

    /// <summary>Runs PortProSage.Service.exe --diagnose &lt;mode&gt; (already existed for
    /// staged command-line testing - see Diagnostics.cs) and shows its console output
    /// in a dialog. Reuses that existing diagnostic rather than adding a new one:
    /// it already does exactly "connect and report success/failure", using whatever
    /// is currently saved in appsettings.json/.Local.json - the same file-based
    /// contract every other feature on this screen follows (this button tests what's
    /// SAVED, not unsaved edits still sitting in the fields - save first if you just
    /// changed something).</summary>
    private void RunConnectionTest(string diagnoseMode, string title)
    {
        if (!File.Exists(ServiceExePath))
        {
            MessageBox.Show(this, $"Could not find PortProSage.Service.exe in:\n{_serviceFolderBox.Text}", "Not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (state, _) = GetServiceRunState();
        if (state != ServiceRunState.NotRunning)
        {
            MessageBox.Show(this,
                "Something is already running (automatic or manual) - a connection test would try to open Sage 50 " +
                "under the same account at the same time, which Sage 50 rejects as a second simultaneous session. " +
                "Stop it first.",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Cursor = Cursors.WaitCursor;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ServiceExePath,
                Arguments = $"--diagnose {diagnoseMode}",
                WorkingDirectory = _serviceFolderBox.Text,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process is null)
            {
                MessageBox.Show(this, "Could not start the test process.", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // Sage 50's OpenDatabase alone can take several seconds - 45s covers
            // that comfortably without leaving the operator staring at a frozen
            // window indefinitely if something really is stuck.
            if (!process.WaitForExit(45000))
            {
                try { process.Kill(); } catch { /* best effort */ }
                MessageBox.Show(this,
                    "The test didn't finish within 45 seconds and was stopped. This can happen if Sage 50 is " +
                    "waiting on a confirmation dialog, or the connection is just unusually slow - check the " +
                    "History & Logs tab's Full log for whatever it managed to do before being stopped.",
                    title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var output = (stdoutTask.Result + stderrTask.Result).Trim();
            var succeeded = process.ExitCode == 0;
            MessageBox.Show(this,
                $"{(succeeded ? "SUCCESS" : "FAILED")}\n\n{(string.IsNullOrWhiteSpace(output) ? "(no output captured)" : output)}",
                title, MessageBoxButtons.OK, succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not run the test:\n{ex.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }
}
