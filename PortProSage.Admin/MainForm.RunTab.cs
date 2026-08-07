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
    private TextBox _runStartInvoice = new() { Width = 160 };
    private TextBox _runEndInvoice = new() { Width = 160 };
    private NumericUpDown _runMaxInvoices = new() { Minimum = 0, Maximum = 100000, Width = 120 };
    private Label _runDryRunStatus = new() { AutoSize = true };
    private Button _manualRunButton = new() { Text = "Manual Run", Width = 140, Height = 36 };
    private Button _manualRunStopButton = new() { Text = "Stop Manual Run", Width = 140, Height = 36, Enabled = false };

    // "Previous Run" section - a read-only snapshot of the most recently completed
    // run's parameters, so it's directly visible (not just documented) that
    // whichever mode was actually used, its values are retained/recorded rather
    // than lost. Refreshed by RefreshPreviousRunSection(), called every time
    // RefreshHistoryList() runs (MainForm.HistoryTab.cs) - initial load, after
    // starting/stopping a Manual Run, and on the result-poll timer.
    private TextBox _prevRunMode = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _prevRunFrom = new() { ReadOnly = true, Enabled = false, Width = 220 };
    private TextBox _prevRunTo = new() { ReadOnly = true, Enabled = false, Width = 220 };
    private TextBox _prevRunMaxInvoices = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _prevRunFirstInvoiceProcessed = new() { ReadOnly = true, Enabled = false, Width = 400 };
    private TextBox _prevRunLastInvoiceProcessed = new() { ReadOnly = true, Enabled = false, Width = 400 };

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
            "Invoice number range"
        });
        _runMode.SelectedIndex = 0;
        _runMode.SelectedIndexChanged += (_, _) => UpdateRunModeFieldStates();

        AddRow(grid, "Mode", _runMode, "(request - not a settings file)", "SyncRequest.FilterType / UseWatermark",
            "Picks how invoices get selected for this one run:\n\n" +
            "• Continue - automatically resumes from wherever the last run stopped. Every run, whatever mode it " +
            "used, records two things when it finishes: PortPro's \"last changed\" timestamp of the newest invoice " +
            "it saw (the watermark), and that invoice's reference number. The NEXT Continue run reads that saved " +
            "watermark back, asks PortPro for everything changed since then up to now, processes it, and only then " +
            "moves the watermark forward again - so as long as every run uses Continue, nothing is skipped and " +
            "nothing is reprocessed. No dates/numbers to set. See the \"Previous Run\" section below to confirm " +
            "what the last run actually recorded.\n" +
            "• Last changed date - invoices whose PortPro \"last updated\" time falls in the From/To window below.\n" +
            "• Invoice number range - invoices whose reference number falls between Start/End invoice number below, " +
            "with BOTH endpoints included (e.g. Start=90, End=95 processes 90, 91, 92, 93, 94, 95 - 6 invoices, not 5).\n\n" +
            "Last changed date and Invoice number range are both one-time overrides - running them never reads or " +
            "changes the saved Continue position, so the next Continue run behaves exactly as if the override run " +
            "never happened.");
        AddRow(grid, "From", _runFrom, "(request)", "SyncRequest.From",
            "Start of the date window - only used by Last changed date mode.\n\n" +
            "Example: set From to 2026-07-01 and To to 2026-07-31 to process everything from July 2026.",
            stretchInput: false);
        AddRow(grid, "To", _runTo, "(request)", "SyncRequest.To",
            "End of the date window - only used by Last changed date mode.\n\n" +
            "Example: set From to 2026-07-01 and To to 2026-07-31 to process everything from July 2026.",
            stretchInput: false);
        AddRow(grid, "Start invoice number", _runStartInvoice, "(request)", "SyncRequest.StartInvoiceNumber",
            "The lowest PortPro reference number to include - only used by Invoice number range mode. Leave blank " +
            "for no lower bound.\n\nExample: RSRE_000102",
            stretchInput: false);
        AddRow(grid, "End invoice number", _runEndInvoice, "(request)", "SyncRequest.EndInvoiceNumber",
            "The highest PortPro reference number to include - only used by Invoice number range mode. Leave blank " +
            "for no upper bound.\n\nExample: Start=90, End=95 processes 90, 91, 92, 93, 94, 95 - 6 invoices (both " +
            "ends included).",
            stretchInput: false);
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

        BuildPreviousRunSection(grid);

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
        _runFrom.Enabled = mode == 1;
        _runTo.Enabled = mode == 1;
        _runStartInvoice.Enabled = mode == 2;
        _runEndInvoice.Enabled = mode == 2;
    }

    /// <summary>Adds the read-only "Previous Run" rows to the same grid as the run
    /// parameters above, rather than a separately-docked panel - a TableLayoutPanel's
    /// rows always render in row-index order, so this sidesteps WinForms' well-known
    /// "last-docked-control-ends-up-on-top" ordering gotcha entirely.</summary>
    private void BuildPreviousRunSection(TableLayoutPanel grid)
    {
        var headingRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label
        {
            Text = "Previous Run",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 18, 3, 2)
        };
        grid.Controls.Add(heading, 0, headingRow);
        grid.SetColumnSpan(heading, 3);

        var subHeadingRow = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var subHeading = new Label
        {
            Text = "Read-only - the parameters and outcome of the most recently completed run (automatic or " +
                   "manual), shown here so it's directly visible that they're retained rather than lost.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 8)
        };
        grid.Controls.Add(subHeading, 0, subHeadingRow);
        grid.SetColumnSpan(subHeading, 3);

        AddRow(grid, "Previous Run: Mode", _prevRunMode, "(history - most recent completed run)", "RunHistoryEntry.Request.FilterType / UseWatermark",
            stretchInput: false);
        AddRow(grid, "Previous Run: From", _prevRunFrom, "(history)", "RunHistoryEntry.Result.WatermarkBeforeRun",
            "The persisted date watermark BEFORE this run started - not the request's own From (which is often " +
            "blank for Invoice number range or Continue mode, where a date window was never part of the request in " +
            "the first place). Together with To, shows how far the \"continue from where we left off\" position " +
            "actually moved.",
            stretchInput: false);
        AddRow(grid, "Previous Run: To", _prevRunTo, "(history)", "RunHistoryEntry.Result.WatermarkAfterRun",
            "The persisted date watermark AFTER this run finished - equal to From if the run didn't advance it (an " +
            "explicit-range or invoice-number run leaves it untouched by design).",
            stretchInput: false);
        AddRow(grid, "Previous Run: Max invoices to process", _prevRunMaxInvoices, "(history)", "RunHistoryEntry.Request.MaxInvoicesToProcess",
            stretchInput: false);
        AddRow(grid, "Previous Run: First Invoice Processed", _prevRunFirstInvoiceProcessed, "(history)", "Parsed from the run's log (TRANSFER lines)",
            "The lowest-numbered invoice actually transferred to Sage 50 during the previous run - same data as the " +
            "History tab's \"Invoice Transferred\" list, parsed from the log rather than result.json so this works " +
            "for automatic-poll runs too (they never write a result.json).",
            stretchInput: false);
        AddRow(grid, "Previous Run: Last Invoice Processed", _prevRunLastInvoiceProcessed, "(history)", "Parsed from the run's log (TRANSFER lines)",
            "The highest-numbered invoice actually transferred to Sage 50 during the previous run.",
            stretchInput: false);
    }

    /// <summary>Called every time RefreshHistoryList() runs (MainForm.HistoryTab.cs) -
    /// initial load, after starting/stopping a Manual Run, and on the result-poll
    /// timer - so this section always reflects the actual most recent run, not a
    /// stale snapshot from when the tab was built.</summary>
    private void RefreshPreviousRunSection()
    {
        var entry = _historyEntries.FirstOrDefault(e => !e.IsPending && e.Result is not null);
        if (entry?.Result is null)
        {
            _prevRunMode.Text = "(no completed run yet)";
            _prevRunFrom.Text = "";
            _prevRunTo.Text = "";
            _prevRunMaxInvoices.Text = "";
            _prevRunFirstInvoiceProcessed.Text = "";
            _prevRunLastInvoiceProcessed.Text = "";
            return;
        }

        var request = entry.Request;
        _prevRunMode.Text = request is null
            ? "(automatic poll - continue from where we left off)"
            : request.UseWatermark ? "Continue (from where we left off)" : request.FilterType.ToString();
        // The persisted date watermark, not request.From/To - the request's own dates
        // are blank for Invoice number range or Continue mode, so showing them left
        // From/To empty for exactly the runs shown in the screenshot that prompted
        // this. The watermark is always populated and reflects the actual date of
        // the last-processed invoice, which is what "From/To" is meant to convey here.
        _prevRunFrom.Text = entry.Result.WatermarkBeforeRun?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(none yet)";
        _prevRunTo.Text = entry.Result.WatermarkAfterRun?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(none yet)";
        _prevRunMaxInvoices.Text = request?.MaxInvoicesToProcess?.ToString() ?? "(no limit)";

        // Parsed from the log, not entry.Result.Outcomes - Outcomes is empty for
        // automatic-poll entries (ReconstructFromLogs only recovers the summary
        // counts, not the per-invoice list), so parsing the log is the only way
        // this works identically for every run source.
        var logLines = string.IsNullOrWhiteSpace(_logFolder)
            ? new List<string>()
            : LogExtractorService.ExtractForWindow(_logFolder, entry.Result.StartedAtUtc, entry.Result.FinishedAtUtc);
        var refs = LogExtractorService.ExtractTransferredInvoices(logLines)
            .Select(r => r.PortProReference)
            .Where(r => !string.IsNullOrEmpty(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        _prevRunFirstInvoiceProcessed.Text = refs.Count > 0 ? refs[0] : "(none)";
        _prevRunLastInvoiceProcessed.Text = refs.Count > 0 ? refs[^1] : "(none)";
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

    /// <summary>Called after a Manual Run finishes or is stopped. Mode, From/To, and
    /// Start/End invoice number are deliberately left exactly as they were - the
    /// Previous Run section (read-only, below) already documents what that run used,
    /// and retaining the live inputs too means re-running the same or a similar
    /// range doesn't require re-entering everything. Only Max invoices to process
    /// resets - it's a one-time safety cap, and silently carrying a small cap
    /// forward into an unrelated later run is the one thing worth clearing
    /// automatically. (The date-window footgun this used to guard against - see
    /// BuildRequestFromForm's case 1 comment - is now closed at the source: "Last
    /// changed date" mode always snaps to whole-day boundaries, so retained dates
    /// can't collapse into a near-zero-width window.)</summary>
    private void ResetRunFormToDefaults()
    {
        _runMaxInvoices.Value = 0;
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
                // Whole calendar days, not the picker's raw Value - the DateTimePicker
                // only ever DISPLAYS a date (no time-of-day control), so "From: June 22,
                // To: June 22" looks like a real window even when the two Values are
                // actually only milliseconds apart (their untouched construction-time
                // default) - confirmed live 2026-08-07 this produced a real run with a
                // 15-millisecond window and, unsurprisingly, 0 invoices fetched.
                // 00:00:01 to 23:59:59 (not midnight-to-midnight) so From is never equal
                // to a boundary the previous day's To could also land on.
                request.From = _runFrom.Value.Date.AddSeconds(1);
                request.To = _runTo.Value.Date.AddDays(1).AddSeconds(-1);
                break;
            case 2:
                request.FilterType = FilterType.InvoiceNumberRange;
                request.StartInvoiceNumber = string.IsNullOrWhiteSpace(_runStartInvoice.Text) ? null : _runStartInvoice.Text.Trim();
                request.EndInvoiceNumber = string.IsNullOrWhiteSpace(_runEndInvoice.Text) ? null : _runEndInvoice.Text.Trim();
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

    /// <summary>Catches a To-before-From or End-invoice-before-Start-invoice range
    /// before it's ever written to a request file - both are silently "valid" as far
    /// as PortProClient's filtering is concerned (an inverted window just matches
    /// nothing), so without this check the run would simply fetch 0 invoices with no
    /// indication why, the same confusing outcome as the earlier zero-width date bug.</summary>
    private static bool ValidateRequestRanges(SyncRequest request, out string? error)
    {
        error = null;

        if (request.From is not null && request.To is not null && request.From > request.To)
        {
            error = $"From ({request.From:yyyy-MM-dd HH:mm:ss}) is after To ({request.To:yyyy-MM-dd HH:mm:ss}) - " +
                    "To must be on or after From.";
            return false;
        }

        if (!string.IsNullOrEmpty(request.StartInvoiceNumber) && !string.IsNullOrEmpty(request.EndInvoiceNumber) &&
            string.CompareOrdinal(request.EndInvoiceNumber, request.StartInvoiceNumber) < 0)
        {
            error = $"End invoice number ({request.EndInvoiceNumber}) is before Start invoice number " +
                    $"({request.StartInvoiceNumber}) - End must be the same as or come after Start.";
            return false;
        }

        return true;
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

        if (!ValidateRequestRanges(request, out var rangeError))
        {
            MessageBox.Show(this, rangeError, "Invalid range", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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
        ResetRunFormToDefaults();
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
