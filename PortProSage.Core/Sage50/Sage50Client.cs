using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;

namespace PortProSage.Core.Sage50;

/// <summary>
/// Talks to the Sage 50 Canadian Edition SDK.
///
/// IMPORTANT - READ BEFORE USING IN PRODUCTION:
/// The Sage 50 CA SDK is a COM component whose exact ProgID, object model, and method
/// names depend on the SDK version installed on this server (it ships with the Sage 50
/// Accounting Partner Program installer and its own help file / sample apps - consult
/// those for your version). Because that surface can't be verified from here, this class
/// uses *late-bound* COM calls (`dynamic`) against a ProgID you configure, with the calls
/// structured around the SDK's typical shape (a Session object you Connect/Open a company
/// with, then Customers/Items/Invoices collections off it). Two things to do before go-live:
///   1. Confirm Sage50Settings.SdkProgId against your installed SDK (check the SDK's
///      registered COM classes, or the ProgID used in its C#/VB sample projects).
///   2. Once confirmed, consider adding a proper COM/Interop reference to the SDK's
///      type library in this project and swapping the `dynamic` calls below for
///      strongly-typed ones - you'll get compile-time checking and IntelliSense.
/// Everything else in this application (PortPro client, validation, orchestration,
/// state tracking) is independent of this class and won't need to change.
/// </summary>
public class Sage50Client : ISage50Client
{
    private readonly Sage50Settings _settings;
    private readonly ILogger<Sage50Client> _logger;
    private dynamic? _session;
    private dynamic? _company;
    private bool _connected;

    public Sage50Client(Sage50Settings settings, ILogger<Sage50Client> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        if (_connected) return Task.CompletedTask;

        _logger.LogInformation("Connecting to Sage 50 via SDK ProgID {ProgId}", _settings.SdkProgId);

        VerifySdkInstallation();

        var comType = Type.GetTypeFromProgID(_settings.SdkProgId)
            ?? throw new InvalidOperationException(
                $"Could not resolve COM ProgID '{_settings.SdkProgId}'. Confirm the Sage 50 SDK is " +
                "installed on this server and that the ProgID matches your SDK version.");

        _session = Activator.CreateInstance(comType)
            ?? throw new InvalidOperationException("Failed to instantiate the Sage 50 SDK session object.");

        // --- TODO: adjust to match your SDK's real connect/login sequence. Typical shape ---
        // (this mirrors the Sage 50 / "Peachtree" SDK pattern, where the calling app
        // registers itself via Begin(appId, appName) before it can open a company -
        // Sage 50 prompts the user, inside Sage 50 itself, to grant that app access
        // the first time it connects; the reference connector tool's "Sage App Name" /
        // "Sage App ID" fields are exactly this):
        // _session.Begin(_settings.AppId, _settings.AppName);
        // _company = _session.Open(_settings.CompanyDataPath, _settings.UserName, _settings.Password);
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.AppId) || string.IsNullOrWhiteSpace(_settings.AppName))
            {
                throw new InvalidOperationException(
                    "Sage50:AppId and Sage50:AppName must be set before connecting - the SDK registers the " +
                    "calling application under these before it will open a company file.");
            }

            _session.Begin(_settings.AppId, _settings.AppName);
            _company = _session.Open(_settings.CompanyDataPath, _settings.UserName, _settings.Password);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to connect/open the Sage 50 company file. Verify CompanyDataPath, UserName, " +
                "Password, AppId, AppName, and that no other exclusive-mode session is blocking access. " +
                "If this is the first time this AppId has connected, check Sage 50 itself for an access " +
                "prompt/dialog that may be waiting for a response.", ex);
        }

        _connected = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Looks up the DLL registered for Sage50Settings.SdkProgId in the Windows
    /// registry (HKEY_CLASSES_ROOT) and logs its actual file version, so a
    /// missing or mismatched SDK install is caught with a clear message at
    /// startup instead of surfacing as an opaque COM error mid-sync. Purely
    /// diagnostic - if ExpectedSdkVersion isn't set, this only logs; if it is
    /// set and doesn't match, this logs a warning (not a hard failure), since
    /// PortPro/Sage don't guarantee the file version string format.
    /// </summary>
    private void VerifySdkInstallation()
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("SDK version check skipped - not running on Windows.");
            return;
        }

        try
        {
            using var progIdKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{_settings.SdkProgId}\CLSID");
            var clsid = progIdKey?.GetValue(null) as string;

            if (clsid is null)
            {
                _logger.LogWarning(
                    "Sage 50 SDK ProgID '{ProgId}' is not registered on this machine. " +
                    "Install the Sage 50 SDK version that matches your installed Sage 50 product " +
                    "before running a real sync (see README for the download portal / version-matching notes).",
                    _settings.SdkProgId);
                return;
            }

            using var inprocKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
            using var localKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\LocalServer32");
            var dllOrExePath = (inprocKey?.GetValue(null) ?? localKey?.GetValue(null)) as string;

            if (string.IsNullOrWhiteSpace(dllOrExePath) || !File.Exists(dllOrExePath))
            {
                _logger.LogWarning(
                    "Sage 50 SDK ProgID '{ProgId}' is registered (CLSID {Clsid}) but its target file " +
                    "could not be located on disk - the install may be broken or partially removed.",
                    _settings.SdkProgId, clsid);
                return;
            }

            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(dllOrExePath);
            var detectedVersion = versionInfo.ProductVersion ?? versionInfo.FileVersion ?? "unknown";

            _logger.LogInformation(
                "Detected Sage 50 SDK: ProgID={ProgId}, file={Path}, version={Version}",
                _settings.SdkProgId, dllOrExePath, detectedVersion);

            if (!string.IsNullOrWhiteSpace(_settings.ExpectedSdkVersion) &&
                !detectedVersion.StartsWith(_settings.ExpectedSdkVersion, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Installed Sage 50 SDK version '{Detected}' does not match the expected version " +
                    "'{Expected}' (Sage50:ExpectedSdkVersion). The SDK must match your installed Sage 50 " +
                    "product's version exactly - confirm both versions and reinstall the matching SDK if needed.",
                    detectedVersion, _settings.ExpectedSdkVersion);
            }
        }
        catch (Exception ex)
        {
            // Diagnostic only - never block startup because this check itself failed.
            _logger.LogWarning(ex, "Could not verify the Sage 50 SDK installation (non-fatal).");
        }
    }

    public Task<Sage50Customer?> FindCustomerByNameAsync(string name, CancellationToken ct)
    {
        EnsureConnected();

        // TODO: replace with the SDK's actual customer lookup, e.g.:
        // var match = _company.Customers.Find(name);
        foreach (var customer in _company!.Customers)
        {
            string customerName = customer.Name;
            if (string.Equals(customerName, name, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<Sage50Customer?>(new Sage50Customer
                {
                    Code = customer.Code,
                    Name = customerName,
                    ReceivableAccount = customer.ReceivableAccount
                });
            }
        }

        return Task.FromResult<Sage50Customer?>(null);
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
            var fakeCode = $"DRYRUN-{Math.Abs(name.GetHashCode()) % 10000}";
            _logger.LogInformation("DRY RUN: would create customer '{Name}' with receivable account '{Account}' (simulated code {Code})",
                name, string.IsNullOrWhiteSpace(receivableAccount) ? _settings.DefaultReceivableAccount : receivableAccount, fakeCode);
            return Task.FromResult(new Sage50Customer { Code = fakeCode, Name = name, ReceivableAccount = receivableAccount });
        }

        // TODO: replace with the SDK's actual customer creation, e.g.:
        // var newCustomer = _company.Customers.Add();
        // newCustomer.Name = name;
        // newCustomer.ReceivableAccount = receivableAccount;
        // newCustomer.Save();
        dynamic newCustomer = _company!.Customers.Add();
        newCustomer.Name = name;
        newCustomer.ReceivableAccount = string.IsNullOrWhiteSpace(receivableAccount)
            ? _settings.DefaultReceivableAccount
            : receivableAccount;
        newCustomer.Save();

        return Task.FromResult(new Sage50Customer
        {
            Code = newCustomer.Code,
            Name = name,
            ReceivableAccount = newCustomer.ReceivableAccount
        });
    }

    public Task<Sage50Item?> FindItemByCodeOrDescriptionAsync(string codeOrDescription, CancellationToken ct)
    {
        EnsureConnected();

        // TODO: replace with the SDK's actual item/service lookup.
        foreach (var item in _company!.Items)
        {
            string code = item.Code;
            string description = item.Description;
            if (string.Equals(code, codeOrDescription, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(description, codeOrDescription, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<Sage50Item?>(new Sage50Item
                {
                    Code = code,
                    Description = description,
                    RevenueAccount = item.RevenueAccount,
                    IsService = true
                });
            }
        }

        return Task.FromResult<Sage50Item?>(null);
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

        // TODO: replace with the SDK's actual item creation, e.g.:
        // var newItem = _company.Items.Add();
        // newItem.Code = code; newItem.Description = description; newItem.Type = ItemType.Service;
        // newItem.RevenueAccount = revenueAccount; newItem.Save();
        dynamic newItem = _company!.Items.Add();
        newItem.Code = code;
        newItem.Description = description;
        newItem.RevenueAccount = string.IsNullOrWhiteSpace(revenueAccount)
            ? _settings.DefaultRevenueAccount
            : revenueAccount;
        newItem.Save();

        return Task.FromResult(new Sage50Item
        {
            Code = code,
            Description = description,
            RevenueAccount = newItem.RevenueAccount,
            IsService = true
        });
    }

    public Task<bool> AccountExistsAsync(string accountNumber, CancellationToken ct)
    {
        EnsureConnected();

        if (string.IsNullOrWhiteSpace(accountNumber)) return Task.FromResult(false);

        // TODO: replace with the SDK's actual GL account lookup, e.g.:
        // return _company.Accounts.Find(accountNumber) != null;
        foreach (var account in _company!.Accounts)
        {
            string number = account.Number;
            if (string.Equals(number, accountNumber, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<bool> InvoiceAlreadyExistsAsync(string externalReference, CancellationToken ct)
    {
        // In practice, checking Sage 50 itself for a prior import is slow (no indexed
        // "external reference" field to search by), so the primary duplicate-check lives
        // in SyncStateRepository (a local table keyed on PortPro invoice id). This method
        // is kept as a defensive secondary check - wire it to whatever field you use on
        // the Sage invoice (e.g. the Invoice's "Comment"/"Reference" field) to store the
        // PortPro reference number, if you want a belt-and-suspenders check against Sage
        // 50 itself.
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

        // TODO: replace with the SDK's actual invoice creation. Typical shape:
        // dynamic newInvoice = _company.SalesInvoices.Add();
        // newInvoice.CustomerCode = invoice.CustomerCode;
        // newInvoice.InvoiceDate = invoice.InvoiceDate;
        // newInvoice.Comment = invoice.ExternalReference; // store PortPro ref for traceability
        // foreach (var line in invoice.Lines)
        // {
        //     dynamic newLine = newInvoice.Lines.Add();
        //     newLine.ItemCode = line.ItemCode;
        //     newLine.Description = line.Description;
        //     newLine.Quantity = line.Quantity;
        //     newLine.UnitPrice = line.UnitPrice;
        //     newLine.Account = line.RevenueAccount;
        //     if (!string.IsNullOrWhiteSpace(line.TaxCode)) newLine.TaxCode = line.TaxCode;
        // }
        // newInvoice.Save();
        // return newInvoice.InvoiceNumber;

        dynamic newInvoice = _company!.SalesInvoices.Add();
        newInvoice.CustomerCode = invoice.CustomerCode;
        newInvoice.InvoiceDate = invoice.InvoiceDate;
        newInvoice.Comment = invoice.ExternalReference;

        foreach (var line in invoice.Lines)
        {
            dynamic newLine = newInvoice.Lines.Add();
            newLine.ItemCode = line.ItemCode;
            newLine.Description = line.Description;
            newLine.Quantity = line.Quantity;
            newLine.UnitPrice = line.UnitPrice;
            newLine.Account = line.RevenueAccount;
            if (!string.IsNullOrWhiteSpace(line.TaxCode))
            {
                newLine.TaxCode = line.TaxCode;
            }
        }

        newInvoice.Save();
        string sageInvoiceNumber = newInvoice.InvoiceNumber;
        return Task.FromResult(sageInvoiceNumber);
    }

    private void EnsureConnected()
    {
        if (!_connected || _company is null)
        {
            throw new InvalidOperationException("Sage50Client.ConnectAsync must succeed before calling this method.");
        }
    }

    public void Dispose()
    {
        try
        {
            _company?.Close();
            _session?.Disconnect();
        }
        catch
        {
            // best-effort cleanup
        }
        finally
        {
            if (_company is not null && Marshal.IsComObject(_company)) Marshal.ReleaseComObject(_company);
            if (_session is not null && Marshal.IsComObject(_session)) Marshal.ReleaseComObject(_session);
        }
    }
}
