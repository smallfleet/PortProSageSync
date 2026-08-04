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

    public Sage50Client(Sage50Settings settings, ILogger<Sage50Client> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        if (_connected) return Task.CompletedTask;

        LogSdkVersion();

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
            // openMultiUserMode: false (exclusive/single-user), NOT a preference -
            // multi-user mode's "ping the Remote Data Access connection manager" step
            // (Simply.ConnectionManagerServiceClient.RegisterChannels) uses .NET
            // Remoting (System.Runtime.Remoting), which was permanently removed in
            // .NET Core/.NET 5+ with no replacement - confirmed live 2026-08-04
            // (FileNotFoundException for System.Runtime.Remoting). Single-user mode
            // takes an exclusive lock on the company file for the duration of the
            // connection, so avoid holding it open longer than necessary.
            opened = SDKInstanceManager.Instance.OpenDatabase(
                _settings.CompanyDataPath,
                _settings.UserName,
                _settings.Password,
                false,
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
            throw new InvalidOperationException(
                "Sage 50 OpenDatabase returned false. Verify CompanyDataPath, UserName, Password, and that no " +
                "other exclusive-mode session is blocking access to the company file.");
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

        var ledger = SDKInstanceManager.Instance.OpenCustomerLedger();
        try
        {
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
        finally
        {
            SDKInstanceManager.Instance.CloseCustomerLedger();
        }
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

        var ledger = SDKInstanceManager.Instance.OpenCustomerLedger();
        try
        {
            ledger.InitializeNew();
            ledger.Name = name;
            ledger.Save();

            return Task.FromResult(new Sage50Customer { Code = name, Name = name, ReceivableAccount = receivableAccount });
        }
        finally
        {
            SDKInstanceManager.Instance.CloseCustomerLedger();
        }
    }

    public Task<Sage50Item?> FindItemByCodeOrDescriptionAsync(string codeOrDescription, CancellationToken ct)
    {
        EnsureConnected();

        var ledger = SDKInstanceManager.Instance.OpenInventoryLedger();
        try
        {
            if (!ledger.LoadByPartCode(codeOrDescription))
            {
                return Task.FromResult<Sage50Item?>(null);
            }

            return Task.FromResult<Sage50Item?>(new Sage50Item
            {
                Code = ledger.Number,
                Description = ledger.Name,
                RevenueAccount = ledger.RevenueAccount,
                IsService = ledger.IsServiceType
            });
        }
        finally
        {
            SDKInstanceManager.Instance.CloseInventoryLedger();
        }
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

        var ledger = SDKInstanceManager.Instance.OpenInventoryLedger();
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
                RevenueAccount = ledger.RevenueAccount,
                IsService = true
            });
        }
        finally
        {
            SDKInstanceManager.Instance.CloseInventoryLedger();
        }
    }

    public Task<bool> AccountExistsAsync(string accountNumber, CancellationToken ct)
    {
        EnsureConnected();

        if (string.IsNullOrWhiteSpace(accountNumber)) return Task.FromResult(false);

        var ledger = SDKInstanceManager.Instance.OpenAccountLedger();
        try
        {
            var found = int.TryParse(accountNumber, out var numeric)
                ? ledger.LoadByAccountNumber(numeric)
                : ledger.LoadByAccountDisplayString(accountNumber);

            return Task.FromResult(found);
        }
        finally
        {
            SDKInstanceManager.Instance.CloseAccountLedger();
        }
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
                string.Join("; ", invoice.Lines.Select(l => $"{l.ItemCode} x{l.Quantity} @ {l.UnitPrice:C} -> {l.RevenueAccount}")));
            return Task.FromResult(fakeInvoiceNumber);
        }

        var journal = SDKInstanceManager.Instance.OpenSalesJournal();
        try
        {
            journal.SelectAPARLedger(invoice.CustomerCode);
            journal.SetReferenceNumber(invoice.ExternalReference);
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

            return Task.FromResult(journal.InvoiceNumber);
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

    public void Dispose()
    {
        if (!_connected) return;

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
