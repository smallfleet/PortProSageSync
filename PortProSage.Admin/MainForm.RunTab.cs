using System.Diagnostics;
using PortProSage.Admin.Models;
using PortProSage.Admin.Services;

namespace PortProSage.Admin;

public partial class MainForm
{
    private string _triggerFolder = "";
    private string _processedTriggerFolder = "";
    private string _logFolder = "";
    private string _manualRunFolder = "";
    private Process? _manualRunProcess;

    private ComboBox _runMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private DateTimePicker _runFrom = new() { Width = 220 };
    private DateTimePicker _runTo = new() { Width = 220 };
    private TextBox _runStartInvoice = new();
    private TextBox _runEndInvoice = new();
    private NumericUpDown _runMaxInvoices = new() { Minimum = 0, Maximum = 100000, Width = 120 };
    private Label _runDryRunStatus = new() { AutoSize = true };
    private Button _manualRunButton = new() { Text = "Manual Run", Width = 140, Height = 36 };
    private Button _manualRunStopButton = new() { Text = "Stop Manual Run", Width = 140, Height = 36, Enabled = false };

    private const string ManualRunHelpText =
        "Runs the sync ONE TIME, right now, in its own dedicated process - it does not write a file for something " +
        "else to notice, and it does not keep running afterward like the Automatic Service does. This is the " +
        "equivalent of running PortProSage.Service.exe --run-once yourself from the command line.\n\n" +
        "Disabled while the Automatic Service is running, and starting a Manual Run disables the Automatic Service " +
        "Start button in turn - both would otherwise try to open Sage 50 under the same configured username at the " +
        "same time, which Sage 50 rejects as a second simultaneous session.\n\n" +
        "Use \"Stop Manual Run\" to interrupt it if it's taking too long or picked up more than intended - it sends " +
        "a graceful shutdown signal first (same as Ctrl+C, so already-imported invoices and the last-processed " +
        "anchor stay correctly recorded up to that point), falling back to a hard stop only if it doesn't respond.";

    private TabPage BuildRunTab()
    {
        var page = new TabPage("Run");
        var grid = NewFieldGrid();

        _runMode.Items.AddRange(new object[]
        {
            "Continue (from where we left off)",
            "Last changed date",
            "Invoice number range",
            "Completed date range"
        });
        _runMode.SelectedIndex = 0;
        _runMode.SelectedIndexChanged += (_, _) => UpdateRunModeFieldStates();

        AddRow(grid, "Mode", _runMode, "(request - not a settings file)", "SyncRequest.FilterType / UseWatermark",
            "Picks how invoices get selected for this one run:\n\n" +
            "• Continue - automatically resumes from wherever the last run stopped (no dates/numbers to set).\n" +
            "• Last changed date - invoices whose PortPro \"last updated\" time falls in the From/To window below.\n" +
            "• Invoice number range - invoices whose reference number falls between Start/End invoice number below.\n" +
            "• Completed date range - invoices whose load-completed date falls in the From/To window below.\n\n" +
            "Every mode except Continue is a one-time override - it never changes the saved \"continue from\" position.");
        AddRow(grid, "From", _runFrom, "(request)", "SyncRequest.From",
            "Start of the date window - only used by Last changed date / Completed date range modes.\n\n" +
            "Example: set From to 2026-07-01 and To to 2026-07-31 to process everything from July 2026.");
        AddRow(grid, "To", _runTo, "(request)", "SyncRequest.To",
            "End of the date window - only used by Last changed date / Completed date range modes.\n\n" +
            "Example: set From to 2026-07-01 and To to 2026-07-31 to process everything from July 2026.");
        AddRow(grid, "Start invoice number", _runStartInvoice, "(request)", "SyncRequest.StartInvoiceNumber",
            "The lowest PortPro reference number to include - only used by Invoice number range mode. Leave blank " +
            "for no lower bound.\n\nExample: RSRE_000102");
        AddRow(grid, "End invoice number", _runEndInvoice, "(request)", "SyncRequest.EndInvoiceNumber",
            "The highest PortPro reference number to include - only used by Invoice number range mode. Leave blank " +
            "for no upper bound.\n\nExample: RSRE_000102 to RSRE_000120 processes those 19 invoices (inclusive).");
        AddRow(grid, "Max invoices to process (0 = no limit)", _runMaxInvoices, "(request)", "SyncRequest.MaxInvoicesToProcess",
            "Caps how many eligible (amount > 0) invoices this run actually processes, on top of whatever Mode " +
            "selects - once this many have been handled, the run stops even if more would otherwise qualify. " +
            "0 means no cap.\n\n" +
            "Example: Continue mode with Max invoices = 10 processes only the next 10 unprocessed invoices, " +
            "even if 50 have changed since the last run.");

        _runDryRunStatus.Text = "Dry run status unknown - load config first.";
        var dryRunRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = "Current write mode:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, dryRunRow);
        grid.Controls.Add(_runDryRunStatus, 1, dryRunRow);

        _manualRunButton.Click += (_, _) => StartManualRun();
        _manualRunStopButton.Click += (_, _) => StopManualRun();
        var manualRunHelp = CreateHelpIcon("Manual Run", ManualRunHelpText);

        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        _manualRunButton.Location = new Point(12, 8);
        _manualRunStopButton.Location = new Point(160, 8);
        manualRunHelp.Location = new Point(310, 15);
        buttonPanel.Controls.Add(_manualRunButton);
        buttonPanel.Controls.Add(_manualRunStopButton);
        buttonPanel.Controls.Add(manualRunHelp);

        var note = new Label
        {
            Text = "Manual Run executes the sync once, immediately, in its own process. It does not depend on (or " +
                   "start) the Automatic Service, and the two can't run at the same time - see the ? icons for why.",
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(12, 8, 12, 0),
            ForeColor = SystemColors.GrayText
        };

        var serviceControlPanel = BuildServiceControlPanel();

        var fieldsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        fieldsScroll.Controls.Add(grid);

        page.Controls.Add(fieldsScroll);
        page.Controls.Add(serviceControlPanel);
        page.Controls.Add(note);
        page.Controls.Add(buttonPanel);

        UpdateRunModeFieldStates();
        RefreshAllTabsFromConfig += () => _runDryRunStatus.Text = _sage50DryRun.Checked ? "DRY RUN (simulated - nothing written to Sage 50)" : "REAL WRITE (changes Sage 50 for real)";
        return page;
    }

    private void UpdateRunModeFieldStates()
    {
        var mode = _runMode.SelectedIndex;
        _runFrom.Enabled = mode == 1 || mode == 3;
        _runTo.Enabled = mode == 1 || mode == 3;
        _runStartInvoice.Enabled = mode == 2;
        _runEndInvoice.Enabled = mode == 2;
    }

    /// <summary>Called after Sync tab (re)loads config - the Run/History tabs need the
    /// real folder paths, not a guess, since that's where the Service actually looks.</summary>
    private void RefreshRunTabFolders()
    {
        if (_appSettings is null) return;
        _triggerFolder = _appSettings.GetString("PortProSage.Sync.TriggerFolder");
        _processedTriggerFolder = _appSettings.GetString("PortProSage.Sync.ProcessedTriggerFolder");
        _logFolder = _appSettings.GetString("PortProSage.Sync.LogFolder");
        // A subfolder of TriggerFolder, not TriggerFolder itself - the Worker's
        // trigger-folder scan is non-recursive, so Manual Run's request/result
        // files here are never picked up (and duplicated) by the Automatic
        // Service's own trigger-watching.
        _manualRunFolder = string.IsNullOrWhiteSpace(_triggerFolder) ? "" : Path.Combine(_triggerFolder, "manual");
        RefreshHistoryList();
    }

    private SyncRequest BuildRequestFromForm()
    {
        var request = new SyncRequest { RequestedBy = Environment.UserName + " (Admin UI - Manual Run)" };

        switch (_runMode.SelectedIndex)
        {
            case 0:
                request.FilterType = FilterType.LastChangedDate;
                request.UseWatermark = true;
                break;
            case 1:
                request.FilterType = FilterType.LastChangedDate;
                request.From = _runFrom.Value;
                request.To = _runTo.Value;
                break;
            case 2:
                request.FilterType = FilterType.InvoiceNumberRange;
                request.StartInvoiceNumber = string.IsNullOrWhiteSpace(_runStartInvoice.Text) ? null : _runStartInvoice.Text.Trim();
                request.EndInvoiceNumber = string.IsNullOrWhiteSpace(_runEndInvoice.Text) ? null : _runEndInvoice.Text.Trim();
                break;
            case 3:
                request.FilterType = FilterType.CompletedDateRange;
                request.From = _runFrom.Value;
                request.To = _runTo.Value;
                break;
        }

        if (_runMaxInvoices.Value > 0)
        {
            request.MaxInvoicesToProcess = (int)_runMaxInvoices.Value;
        }

        return request;
    }

    /// <summary>The actual resolved parameters this run will use - not just "Mode: X",
    /// since that alone doesn't show what dates/numbers/caps were actually resolved
    /// from the form, or which real Sage 50 company file is about to be written to.</summary>
    private string BuildManualRunConfirmationText(SyncRequest request, string requestPathPreview)
    {
        var lines = new List<string>
        {
            "Run this now?",
            "",
            $"Write mode: {(_sage50DryRun.Checked ? "DRY RUN (simulated - nothing written to Sage 50)" : "REAL WRITE (changes Sage 50 for real)")}",
            $"Sage 50 company file: {_sage50CompanyDataPath.Text}",
            $"Mode: {_runMode.SelectedItem}"
        };

        if (request.UseWatermark)
        {
            lines.Add("Resolves \"continue from where we left off\" using the persisted watermark - not visible until the Service resolves it.");
        }
        if (request.From is not null || request.To is not null)
        {
            lines.Add($"From: {request.From:yyyy-MM-dd HH:mm}   To: {request.To:yyyy-MM-dd HH:mm}");
        }
        if (request.StartInvoiceNumber is not null || request.EndInvoiceNumber is not null)
        {
            lines.Add($"Start invoice: {request.StartInvoiceNumber ?? "(none)"}   End invoice: {request.EndInvoiceNumber ?? "(none)"}");
        }
        lines.Add($"Max invoices to process: {(request.MaxInvoicesToProcess?.ToString() ?? "no limit")}");
        lines.Add("");
        lines.Add($"Request will be written to:\n{requestPathPreview}");

        return string.Join(Environment.NewLine, lines);
    }

    private void StartManualRun()
    {
        if (string.IsNullOrWhiteSpace(_manualRunFolder))
        {
            MessageBox.Show(this, "Load the Service config first (Sync tab) so the trigger folder is known.", "Not ready",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (state, _) = GetServiceRunState();
        if (state != ServiceRunState.NotRunning)
        {
            MessageBox.Show(this,
                "Something is already running (automatic or manual) - Manual Run and the Automatic Service can't " +
                "run at the same time, since both connect to Sage 50 under the same account.",
                "Already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!File.Exists(ServiceExePath))
        {
            MessageBox.Show(this, $"Could not find PortProSage.Service.exe in:\n{_serviceFolderBox.Text}", "Not found",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var request = BuildRequestFromForm();
        var requestPathPreview = Path.Combine(_manualRunFolder, $"{request.RequestId}.request.json");

        var confirm = MessageBox.Show(this, BuildManualRunConfirmationText(request, requestPathPreview),
            "Confirm manual run", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        Directory.CreateDirectory(_manualRunFolder);
        var requestPath = TriggerService.WriteRequest(_manualRunFolder, request);

        _manualRunProcess = Process.Start(new ProcessStartInfo
        {
            FileName = ServiceExePath,
            Arguments = $"--run-once \"{requestPath}\"",
            WorkingDirectory = _serviceFolderBox.Text,
            UseShellExecute = true // its own console window, visible to the operator while it runs
        });

        _pendingRequestId = request.RequestId;
        _pendingProcessedFolder = _manualRunFolder;
        _resultPollTimer.Start();

        // Set immediately, not just via the next RefreshServiceStatus() tick -
        // closes any small timing gap where WMI might not yet see the
        // just-launched process's command line right after Process.Start().
        _manualRunButton.Enabled = false;
        _manualRunStopButton.Enabled = true;
        _startServiceButton.Enabled = false;
        _stopServiceButton.Enabled = false;

        RefreshServiceStatus();

        SelectHistoryTab();
        RefreshHistoryList();
    }

    private void StopManualRun()
    {
        if (_manualRunProcess is null) { RefreshServiceStatus(); return; }

        var confirm = MessageBox.Show(this,
            $"Stop this manual run (PID {_manualRunProcess.Id}) now?\n\n" +
            "A graceful shutdown is requested first, so already-imported invoices and the last-processed anchor " +
            "stay correctly recorded up to whatever point it's reached.",
            "Confirm stop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        GracefulStop(_manualRunProcess);
        _resultPollTimer.Stop();
        _pendingRequestId = null;
        RefreshServiceStatus();
        RefreshHistoryList();
    }

    /// <summary>Called by RefreshServiceStatus() (MainForm.ServiceControl.cs) every time
    /// it re-checks what's actually running, so Manual Run's buttons always reflect
    /// reality - including a manual run that finished, or one started outside this app.</summary>
    private void UpdateManualRunButtonStates(ServiceRunState state, Process? process)
    {
        if (state == ServiceRunState.ManualRunning)
        {
            _manualRunButton.Enabled = false;
            _manualRunStopButton.Enabled = true;
            _manualRunProcess = process;
        }
        else
        {
            _manualRunStopButton.Enabled = false;
            _manualRunProcess = null;
            _manualRunButton.Enabled = state == ServiceRunState.NotRunning;

            if (_pendingRequestId is not null && state == ServiceRunState.NotRunning)
            {
                // A manual run we were tracking just finished (or was stopped) -
                // one more history refresh in case the result file appeared just
                // after the process exited.
                RefreshHistoryList();
            }
        }
    }
}
