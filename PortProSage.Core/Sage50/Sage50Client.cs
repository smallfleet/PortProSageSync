using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using SimplySDK;

namespace PortProSage.Core.Sage50;

/// <summary>
/// Talks to the Sage 50 Canadian Edition SDK ("SimplySDK" namespace, ships as
/// Sage_SA.SDK.dll).
///
/// Confirmed 2026-08-04 against the SDK's own shipped XML documentation
/// (extracted from the working "Sage50-PortPro connector" tool's install) and
/// cross-checked live via ConnectAsync/FindCustomerByNameAsync against this
/// server's real dev company file:
///   - This is a plain managed .NET assembly, NOT a COM component - there is no
///     ProgID to resolve. SDKInstanceManager.Instance is a singleton; call
///     OpenDatabase(...) once, then OpenXxxLedger()/OpenXxxJournal() to get
///     working ledger/journal objects, then CloseDatabase() when done.
///   - Ledgers (CustomerLedger, AccountLedger, InventoryLedger, ...) share a
///     common LedgerBase: LoadByXxx(...) for lookup, InitializeNew()+Save() to
///     create.
///   - SalesJournal is a "mimic the on-screen Sales Journal form" API: select
///     the customer, set each line's item/qty/price/account, then Post().
///
/// What's still unconfirmed (no real write has been attempted against this SDK
/// yet - see README's staged testing plan, stage 5): the exact CreateCustomerAsync
/// /CreateServiceItemAsync/CreateInvoiceAsync sequences below are built from the
/// SDK's documented method signatures (so the shapes and types are real, and the
/// compiler enforces them against the actual assembly), but haven't been
/// exercised end-to-end against a real write. Confirm with a small manual test
/// (DryRun off, one invoice) before trusting a batch.
/// </summary>
public class Sage50Client : ISage50Client
{
    private readonly Sage50Settings _settings;
    private readonly ILogger<Sage50Client> _logger;
    private bool _connected;

    // Opened once, lazily, on first use and kept open for as long as this
    // client stays connected (same lifetime as OpenDatabase/CloseDatabase
    // itself - see ConnectAsync's doc comment on why the database connection
    // is already long-lived, not per-call) - NOT closed and reopened around
    // every single FindXxx/CreateXxx call like before. Confirmed live
    // 2026-08-10: opening/closing a ledger has real, measurable overhead
    // (each open/close round-trips into the Sage 50 engine), and a 120-
    // invoice run was doing this 2-10+ times PER invoice (once for the
    // customer lookup, once per pricing line for its item lookup, once per
    // resolved account) - confirmed via the SDK's own docs that
    // CustomerLedger/InventoryLedger/AccountLedger are meant to be reused
    // this way ("LedgerBase: LoadByXxx() for lookup, InitializeNew()+Save()
    // to create"), the same pattern the top-level database connection
    // already relies on. Deliberately NOT applied to SalesJournal
    // (OpenSalesJournal/CloseSalesJournal, in CreateInvoiceAsync) - that's
    // the actual invoice POST, the SDK docs don't show a "reset for the next
    // entry" method on it, and this file already has real incident history
    // (see TerminateOnFatalWriteError) of a write leaving the SDK session in
    // a bad state; a fresh journal per invoice is the safer trade here.
    private SimplySDK.ReceivableModule.CustomerLedger? _customerLedger;
    private SimplySDK.InventoryModule.InventoryLedger? _inventoryLedger;
    private SimplySDK.GeneralModule.AccountLedger? _accountLedger;

    private SimplySDK.ReceivableModule.CustomerLedger GetCustomerLedger() => _customerLedger ??= SDKInstanceManager.Instance.OpenCustomerLedger();
    private SimplySDK.InventoryModule.InventoryLedger GetInventoryLedger() => _inventoryLedger ??= SDKInstanceManager.Instance.OpenInventoryLedger();
    private SimplySDK.GeneralModule.AccountLedger GetAccountLedger() => _accountLedger ??= SDKInstanceManager.Instance.OpenAccountLedger();

    public Sage50Client(Sage50Settings settings, ILogger<Sage50Client> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        if (_connected) return Task.CompletedTask;

        LogSdkVersion();

        // Must be set before OpenDatabase - the default SDKAlert implementation
        // throws for every alert, including ordinary Yes/No confirmations Sage 50's
        // desktop UI would show as a dialog to a human. See HeadlessSdkAlert.
        SDKInstanceManager.Instance.SetAlertImplementation(new HeadlessSdkAlert(_logger));

        if (string.IsNullOrWhiteSpace(_settings.AppId) || string.IsNullOrWhiteSpace(_settings.AppName))
        {
            throw new InvalidOperationException(
                "Sage50:AppId (the SDK's 'TPAppCode' - max 6 characters) and Sage50:AppName (the SDK's " +
                "'TPAppName') must be set before connecting.");
        }

        if (_settings.AppId.Length > 6)
        {
            throw new InvalidOperationException(
                $"Sage50:AppId '{_settings.AppId}' is {_settings.AppId.Length} characters - the Sage 50 SDK's " +
                "TPAppCode parameter accepts a maximum of 6.");
        }

        _logger.LogInformation("Opening Sage 50 company file {Path}", _settings.CompanyDataPath);

        bool opened;
        try
        {
            // openMultiUserMode: true - confirmed working live 2026-08-04, including
            // a genuine concurrency test: this service connected successfully as
            // Sage50:UserName ("PortProConnect", a dedicated account - see below)
            // while a real interactive Sage 50 Accounting session stayed open the
            // whole time under "sysadmin". It had been forced to false earlier
            // because multi-user's "ping the Remote Data Access connection manager"
            // step uses .NET Remoting (System.Runtime.Remoting), unavailable under
            // .NET Core/.NET 5+ - but that's *why* this project retargeted to net48
            // in the first place, and Remoting is natively available there.
            //
            // IMPORTANT: Sage50:UserName must be a dedicated Sage 50 user account,
            // never the same account a human logs in with interactively - Sage 50
            // rejects a second simultaneous session under the SAME username even in
            // multi-user mode ("Someone else is already using the program under
            // this name"), confirmed live when this was still configured as
            // "sysadmin". Single-user mode, for reference, takes an exclusive lock
            // on the company file for as long as this connection stays open, which -
            // since Sage50Client is a DI singleton that stays connected once first
            // connected - would lock out the real Sage 50 Accounting desktop app
            // entirely for as long as this service runs.
            opened = SDKInstanceManager.Instance.OpenDatabase(
                _settings.CompanyDataPath,
                _settings.UserName,
                _settings.Password,
                true,
                _settings.AppName,
                _settings.AppId,
                1);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to open the Sage 50 company file. Verify Sage50:CompanyDataPath/UserName/Password/AppId/" +
                "AppName in appsettings, and confirm no other exclusive-mode session is blocking access.", ex);
        }

        if (!opened)
        {
            // Confirmed live 2026-08-07 the original one-line message here ("OpenDatabase
            // returned false...") wasn't actionable for a non-developer reading the log -
            // it names the settings to check but not what's actually likely wrong or what
            // to do about it. Spell out the real, ranked causes instead.
            throw new InvalidOperationException(
                $"Could not open the Sage 50 company file '{_settings.CompanyDataPath}' as user " +
                $"'{_settings.UserName}'. This almost always means one of:\n" +
                $"  1. Sage 50 is already open on this computer under the SAME username ('{_settings.UserName}') " +
                "- Sage 50 refuses a second simultaneous session under one username. Close that session, or " +
                "(recommended) configure a separate, dedicated Sage 50 user account for this service in the " +
                "Admin app's Sage 50 tab so a human can stay signed in under their own account at the same time.\n" +
                "  2. The company data path, username, or password configured in the Admin app's Sage 50 tab is " +
                "wrong - try opening the company file by hand in Sage 50 with the same username/password to " +
                "confirm they work.\n" +
                "  3. Sage 50 itself isn't installed, licensed, or activated on this computer.");
        }

        _connected = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Logs the file version of the bundled Sage_SA.SDK.dll (lib/Sage50SDK) - the
    /// replacement for the old COM-registry version check, since this SDK is now
    /// referenced directly rather than resolved via a registered ProgID. This SDK
    /// must match the Sage 50 product version installed on whatever machine runs
    /// the built service - see README "Sage 50 SDK installation & version matching".
    /// </summary>
    private void LogSdkVersion()
    {
        try
        {
            var location = typeof(SDKInstanceManager).Assembly.Location;
            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(location);
            var detectedVersion = versionInfo.ProductVersion ?? versionInfo.FileVersion ?? "unknown";

            _logger.LogInformation("Using bundled Sage 50 SDK: {Path}, version={Version}", location, detectedVersion);

            if (!string.IsNullOrWhiteSpace(_settings.ExpectedSdkVersion) &&
                !detectedVersion.StartsWith(_settings.ExpectedSdkVersion, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Bundled Sage 50 SDK version '{Detected}' does not match Sage50:ExpectedSdkVersion " +
                    "'{Expected}' - this SDK must match the Sage 50 product version installed on THIS machine, " +
                    "or opening the company file may fail. Re-populate lib/Sage50SDK from a matching install if so.",
                    detectedVersion, _settings.ExpectedSdkVersion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine the bundled Sage 50 SDK version (non-fatal).");
        }
    }

    public Task<Sage50Customer?> FindCustomerByNameAsync(string name, CancellationToken ct)
    {
        EnsureConnected();

        var ledger = GetCustomerLedger();
        if (!ledger.LoadByName(name))
        {
            return Task.FromResult<Sage50Customer?>(null);
        }

        // CustomerLedger (via APARLedgerBase) exposes Name plus address/contact
        // fields - there's no separate "customer code" distinct from Name in this
        // SDK, and no per-customer receivable-account override (Simply
        // Accounting/Sage 50 posts all customers to one AR control account), so
        // ReceivableAccount is left null here; Sage50Settings.DefaultReceivableAccount
        // is what actually matters for posting.
        return Task.FromResult<Sage50Customer?>(new Sage50Customer
        {
            Code = ledger.Name,
            Name = ledger.Name,
            ReceivableAccount = null
        });
    }

    public Task<Sage50Customer> CreateCustomerAsync(string name, string receivableAccount, CancellationToken ct)
    {
        EnsureConnected();

        if (!_settings.AutoCreateCustomers)
        {
            throw new InvalidOperationException(
                $"Customer '{name}' does not exist in Sage 50 and AutoCreateCustomers is disabled.");
        }

        _logger.LogInformation("Creating Sage 50 customer '{Name}'", name);

        if (_settings.DryRun)
        {
            _logger.LogInformation("DRY RUN: would create customer '{Name}'", name);
            return Task.FromResult(new Sage50Customer { Code = name, Name = name, ReceivableAccount = receivableAccount });
        }

        var ledger = GetCustomerLedger();
        try
        {
            ledger.InitializeNew();
            ledger.Name = name;
            ledger.Save();

            return Task.FromResult(new Sage50Customer { Code = name, Name = name, ReceivableAccount = receivableAccount });
        }
        catch (Exception ex)
        {
            TerminateOnFatalWriteError($"creating customer '{name}'", ex);
            throw; // unreachable - TerminateOnFatalWriteError never returns
        }
    }

    public Task<Sage50Item?> FindItemByCodeOrDescriptionAsync(string codeOrDescription, CancellationToken ct)
    {
        EnsureConnected();

        var ledger = GetInventoryLedger();
        if (!ledger.LoadByPartCode(codeOrDescription))
        {
            return Task.FromResult<Sage50Item?>(null);
        }

        return Task.FromResult<Sage50Item?>(new Sage50Item
        {
            Code = ledger.Number,
            Description = ledger.Name,
            RevenueAccount = ExtractAccountNumber(ledger.RevenueAccount),
            IsService = ledger.IsServiceType
        });
    }

    public Task<Sage50Item> CreateServiceItemAsync(string code, string description, string revenueAccount, CancellationToken ct)
    {
        EnsureConnected();

        if (!_settings.AutoCreateItems)
        {
            throw new InvalidOperationException(
                $"Item/service '{description}' does not exist in Sage 50 and AutoCreateItems is disabled.");
        }

        _logger.LogInformation("Creating Sage 50 service item '{Code}' - '{Description}'", code, description);

        if (_settings.DryRun)
        {
            _logger.LogInformation("DRY RUN: would create service item '{Code}' - '{Description}' with revenue account '{Account}'",
                code, description, string.IsNullOrWhiteSpace(revenueAccount) ? _settings.DefaultRevenueAccount : revenueAccount);
            return Task.FromResult(new Sage50Item { Code = code, Description = description, RevenueAccount = revenueAccount, IsService = true });
        }

        var ledger = GetInventoryLedger();
        try
        {
            ledger.InitializeNew();
            ledger.Number = code;
            ledger.Name = description;
            ledger.IsServiceType = true;
            ledger.RevenueAccount = string.IsNullOrWhiteSpace(revenueAccount) ? _settings.DefaultRevenueAccount : revenueAccount;
            ledger.Save();

            return Task.FromResult(new Sage50Item
            {
                Code = ledger.Number,
                Description = ledger.Name,
                RevenueAccount = ExtractAccountNumber(ledger.RevenueAccount),
                IsService = true
            });
        }
        catch (Exception ex)
        {
            TerminateOnFatalWriteError($"creating service item '{code}' ('{description}')", ex);
            throw; // unreachable - TerminateOnFatalWriteError never returns
        }
    }

    public Task<bool> AccountExistsAsync(string accountNumber, CancellationToken ct)
    {
        EnsureConnected();

        if (string.IsNullOrWhiteSpace(accountNumber)) return Task.FromResult(false);

        // Confirmed-but-SDK-unverifiable accounts (currency-paired accounts like this
        // company's 4100/4110 - see AccountsUnverifiableBySdk's doc comment) bypass
        // the SDK lookup entirely, since it's confirmed to always return false for them
        // regardless of method/format/call order.
        if (_settings.AccountsUnverifiableBySdk.Contains(accountNumber, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        var ledger = GetAccountLedger();
        // LoadByAccountNumber, not LoadByAccountDisplayString - confirmed live
        // 2026-08-04 by testing both against 9 real accounts from this company's
        // actual chart of accounts: LoadByAccountDisplayString returned false for
        // every single one (including accounts LoadByAccountNumber correctly
        // found), so it's not usable here in whatever form we tried.
        var found = int.TryParse(accountNumber, out var numeric)
            ? ledger.LoadByAccountNumber(numeric)
            : ledger.LoadByAccountDisplayString(accountNumber);
        return Task.FromResult(found);
    }

    /// <summary>
    /// Reading an account back off an existing ledger record (e.g. InventoryLedger.
    /// RevenueAccount) returns the SDK's display string "NUMBER NAME" (e.g. "4100
    /// Sales Revenue-CDN"), not the bare number - confirmed live 2026-08-04, and it
    /// broke both AccountExistsAsync's AccountsUnverifiableBySdk bypass (an exact-
    /// string match against bare "4100") and SetLineAccount (whose SDK docs say it
    /// takes "account number in string", not a display string). Strips everything
    /// from the first space onward so callers consistently see the bare number.
    /// </summary>
    private static string ExtractAccountNumber(string? accountDisplayString)
    {
        var value = accountDisplayString ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var spaceIndex = value.IndexOf(' ');
        return spaceIndex < 0 ? value : value.Substring(0, spaceIndex);
    }

    public Task<bool> InvoiceAlreadyExistsAsync(string externalReference, CancellationToken ct)
    {
        // In practice, checking Sage 50 itself for a prior import is slow, so the
        // primary duplicate-check lives in SyncStateRepository (a local table keyed
        // on PortPro invoice id). This is kept as a defensive secondary check -
        // InvoiceJournal.LoadInvoiceForLookup(reference, ...) could wire this up for
        // real if you want a belt-and-suspenders check against Sage 50 itself.
        return Task.FromResult(false);
    }

    public Task<string> CreateInvoiceAsync(Sage50Invoice invoice, CancellationToken ct)
    {
        EnsureConnected();

        _logger.LogInformation(
            "Creating Sage 50 sales invoice for customer {Customer}, external ref {Ref}, {LineCount} line(s)",
            invoice.CustomerCode, invoice.ExternalReference, invoice.Lines.Count);

        if (_settings.DryRun)
        {
            var fakeInvoiceNumber = $"DRYRUN-{invoice.ExternalReference}";
            _logger.LogInformation(
                "DRY RUN: would create invoice {FakeNumber} for customer {Customer}, date {Date}, lines: {Lines}",
                fakeInvoiceNumber, invoice.CustomerCode, invoice.InvoiceDate.ToShortDateString(),
                string.Join("; ", invoice.Lines.Select(l => $"{l.ItemCode} x{l.Quantity} @ {l.UnitPrice:C} -> {l.RevenueAccount}" + (string.IsNullOrWhiteSpace(l.TaxCode) ? "" : $" [tax:{l.TaxCode}]"))));
            return Task.FromResult(fakeInvoiceNumber);
        }

        var journal = SDKInstanceManager.Instance.OpenSalesJournal();
        try
        {
            // SelectTransType must be called before anything else - InvoiceJournal is
            // a combined Quote/Order/Invoice journal (see get_IsInvoice/get_IsOrder/
            // get_IsQuote) and defaults to a mode where SetReferenceNumber and other
            // invoice-specific fields throw SimplyNoAccessException ("not accessible
            // under current circumstance") - confirmed live 2026-08-04 and against the
            // SDK's own shipped XML docs: 0 = Invoice, 1 = Order, 2 = Quote. This was
            // the root cause of the original incident's SetReferenceNumber failure.
            journal.SelectTransType((short)0);

            // Returns the selected customer's ID, or a non-positive value if the name
            // didn't resolve to an actual customer - confirmed via the SDK's own docs
            // ("ID of the vendor or customer selected"). Not checking this let the
            // journal continue in an invalid state on a bad customer name instead of
            // failing with a clear error immediately.
            var selectedCustomerId = journal.SelectAPARLedger(invoice.CustomerCode);
            if (selectedCustomerId <= 0)
            {
                throw new InvalidOperationException(
                    $"Sage 50 did not select a valid customer for '{invoice.CustomerCode}' (SelectAPARLedger returned {selectedCustomerId}) - cannot post invoice for external ref '{invoice.ExternalReference}'.");
            }

            // NOT SetReferenceNumber - per the SDK's own docs that method is "the
            // reference number for the prepayment or deposit", an unrelated field.
            // InvoiceNumber is the actual Sage 50 invoice number (a settable string
            // property, not necessarily auto-incrementing) - setting it directly to
            // PortPro's reference achieves the traceability this was meant for, and
            // matches CreateInvoiceAsync's existing return value below. Confirmed
            // live 2026-08-04: SetReferenceNumber was the root cause of both the
            // original incident's failure and this canary's repeated failures - it
            // throws SimplyNoAccessException for a plain invoice with no prepayment.
            journal.InvoiceNumber = invoice.ExternalReference;
            journal.SetJournalDate(invoice.InvoiceDate.ToString("yyyy-MM-dd"));

            var line = 1;
            foreach (var l in invoice.Lines)
            {
                if (!string.IsNullOrWhiteSpace(l.ItemCode))
                {
                    journal.SetItemNumber(l.ItemCode, line);
                }

                journal.SetDescription(l.Description, line);
                journal.SetQuantity((double)l.Quantity, line);
                journal.SetPrice((double)l.UnitPrice, line);
                journal.SetLineAccount(l.RevenueAccount, line);

                if (!string.IsNullOrWhiteSpace(l.TaxCode))
                {
                    journal.SetTaxCodeString(l.TaxCode, line);
                }

                line++;
            }

            if (!journal.Post())
            {
                throw new InvalidOperationException("Sage 50 SalesJournal.Post() returned false.");
            }

            // NOT journal.InvoiceNumber - confirmed live 2026-08-04 that re-reading
            // it after Post() returns an unreliable value unrelated to the real,
            // correctly-saved invoice number (verified directly in Sage 50: the
            // actual "Invoice No." field matches invoice.ExternalReference exactly,
            // e.g. "RSRE_000102", not whatever journal.InvoiceNumber echoed back).
            // What we set before Post() is what's actually saved, so return that.
            return Task.FromResult(invoice.ExternalReference);
        }
        catch (Exception ex)
        {
            // Not a sign of a compromised session - Sage 50's own duplicate-number
            // validation correctly rejected this before writing anything. Confirmed
            // live 2026-08-05: this happens for invoices that were already posted in
            // an earlier run (e.g. the original incident's cascade, before per-run
            // ascending-order processing existed) - recoverable by the caller
            // (SyncOrchestrator) marking it imported and moving on, not fatal.
            if (ex.Message.IndexOf("invoice number has already been used", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new DuplicateInvoiceNumberException(
                    $"Invoice number '{invoice.ExternalReference}' already exists in Sage 50 for customer '{invoice.CustomerCode}'.", ex);
            }

            TerminateOnFatalWriteError($"posting invoice for external ref '{invoice.ExternalReference}'", ex);
            throw; // unreachable - TerminateOnFatalWriteError never returns
        }
        finally
        {
            SDKInstanceManager.Instance.CloseSalesJournal();
        }
    }

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Sage50Client.ConnectAsync must succeed before calling this method.");
        }
    }

    /// <summary>
    /// Terminates the whole process immediately - deliberate, not a bug. Confirmed
    /// live 2026-08-05: a real Save()/Post() failure here (a SimplyNoAccessException
    /// on SetReferenceNumber, root cause still not understood) left the SDK session
    /// itself in a bad state - every subsequent SDK call in the same session started
    /// throwing a generic "Sage 50 will now shut down" error too, yet the caller
    /// (InvoiceValidationService/SyncOrchestrator) was catching each failure
    /// per-invoice and continuing on to the next one regardless, cascading through
    /// ~30 more invoices against an already-compromised session with no way to tell
    /// from the logs alone which of those "succeeded" for real.
    ///
    /// Rather than try to classify which write failures are "safe to continue past"
    /// (we don't have enough evidence to know), ANY exception from a real (non-
    /// DryRun) Create*/Post call now kills the process outright. This also gives
    /// correct watermark/last-processed-invoice-number behaviour for free: that
    /// tracking only advances in a batch at the very end of SyncOrchestrator.RunAsync,
    /// after the per-invoice loop - dying mid-loop means it's never reached, so nothing
    /// this run touched successfully-but-unrecorded gets silently skipped by a later
    /// run. Invoices individually completed before the failure are already durably
    /// recorded via SyncStateRepository.MarkImported (which happens immediately per
    /// invoice, not batched), so a restart cleanly resumes at the point of failure -
    /// nothing already-imported gets reprocessed, nothing unprocessed gets skipped.
    /// </summary>
    private void TerminateOnFatalWriteError(string operation, Exception ex)
    {
        _logger.LogCritical(ex,
            "FATAL: Sage 50 write failed while {Operation}. Terminating the process immediately rather than " +
            "continuing against a possibly-compromised SDK session - restart the service to resume; already-" +
            "imported invoices won't be reprocessed, and nothing after this point in the current run was recorded " +
            "as processed.", operation);

        // Redundant, unbuffered channel in addition to the logger above, in case the
        // Serilog file sink hasn't flushed by the time Environment.Exit tears down
        // the process.
        Console.Error.WriteLine($"FATAL: Sage 50 write failed while {operation}: {ex}");
        Console.Error.Flush();

        Environment.Exit(1);
    }

    public void Dispose()
    {
        if (!_connected) return;

        // Close whichever of the long-lived ledgers actually got opened (see
        // GetCustomerLedger/GetInventoryLedger/GetAccountLedger) before closing
        // the database itself - best-effort, same as CloseDatabase below; the
        // process is going away either way, so a failure here just isn't worth
        // more than a swallowed exception.
        if (_customerLedger is not null) { try { SDKInstanceManager.Instance.CloseCustomerLedger(); } catch { } }
        if (_inventoryLedger is not null) { try { SDKInstanceManager.Instance.CloseInventoryLedger(); } catch { } }
        if (_accountLedger is not null) { try { SDKInstanceManager.Instance.CloseAccountLedger(); } catch { } }

        try
        {
            SDKInstanceManager.Instance.CloseDatabase();
        }
        catch
        {
            // best-effort cleanup
        }
        finally
        {
            _connected = false;
        }
    }
}
