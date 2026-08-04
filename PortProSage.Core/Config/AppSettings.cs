namespace PortProSage.Core.Config;

/// <summary>
/// Root configuration object, bound from appsettings.json ("PortProSage" section).
/// </summary>
public class AppSettings
{
    public PortProSettings PortPro { get; set; } = new();
    public Sage50Settings Sage50 { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();

    /// <summary>
    /// Placeholder for the future Fixyee integration - not wired into the sync
    /// pipeline yet. Fill in the API key/base URL when you're ready to build
    /// FixyeeClient against their real API.
    /// </summary>
    public FixyeeSettings Fixyee { get; set; } = new();
}

public class FixyeeSettings
{
    /// <summary>Base URL for the Fixyee API, once known.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API key issued by Fixyee for this integration.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Set to true once Fixyee credentials are configured and you want the sync to target it too.</summary>
    public bool Enabled { get; set; } = false;
}


public class PortProSettings
{
    /// <summary>Base URL for the PortPro API, e.g. https://api1.app.portpro.io/v1</summary>
    public string BaseUrl { get; set; } = "https://api1.app.portpro.io/v1";

    /// <summary>Path (relative to BaseUrl) for fetching invoices, e.g. /invoices</summary>
    public string InvoiceEndpoint { get; set; } = "/invoices";

    /// <summary>Path (relative to BaseUrl) used to validate/exchange the initial access token, e.g. /token</summary>
    public string AccessTokenEndpoint { get; set; } = "/token";

    /// <summary>Path (relative to BaseUrl) used to obtain a new access token from a refresh token, e.g. /generate-new-token</summary>
    public string NewTokenEndpoint { get; set; } = "/generate-new-token";

    /// <summary>
    /// Access token issued directly by PortPro (no client id/secret exchange in this
    /// account's setup - PortPro hands out an access/refresh token pair directly,
    /// typically from within PortPro's own integration/API settings screen, or from
    /// your PortPro account rep). Paste the current one here to start; the service
    /// will call NewTokenEndpoint with RefreshToken to get a new one once this expires.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Refresh token issued alongside the access token above.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>How many invoices to request per page from PortPro.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>HTTP timeout, in seconds, for PortPro calls.</summary>
    public int TimeoutSeconds { get; set; } = 60;
}

public class Sage50Settings
{
    /// <summary>
    /// Path to the Sage 50 Canadian Edition company data file (the .SAI file),
    /// e.g. C:\Users\Public\Documents\Sage\Simply Accounting\2026\Samdata\Premium\Company.SAI
    /// </summary>
    public string CompanyDataPath { get; set; } = string.Empty;

    /// <summary>Sage 50 system login user name (must have a user account with API/SDK access rights).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Sage 50 system login password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The Sage 50 SDK's "TPAppName" (third-party application name) parameter,
    /// passed to SDKInstanceManager.OpenDatabase alongside AppId.
    /// </summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>
    /// The Sage 50 SDK's "TPAppCode" parameter - a short identifier for this
    /// application, passed to SDKInstanceManager.OpenDatabase alongside AppName.
    /// Maximum 6 characters (enforced by the SDK).
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// Optional. If set (e.g. "2026.2"), the service logs a warning at startup when
    /// the bundled Sage 50 SDK's file version (lib/Sage50SDK/Sage_SA.SDK.dll) doesn't
    /// start with this value - that SDK must match the Sage 50 product version
    /// installed on whatever machine runs the built service. Leave blank to skip
    /// the check.
    /// </summary>
    public string ExpectedSdkVersion { get; set; } = string.Empty;

    /// <summary>Default GL revenue account to use when a matched item has no account of its own.</summary>
    public string DefaultRevenueAccount { get; set; } = string.Empty;

    /// <summary>Default GL receivable account for auto-created customers.</summary>
    public string DefaultReceivableAccount { get; set; } = string.Empty;

    /// <summary>Whether the integration is allowed to create missing customers automatically.</summary>
    public bool AutoCreateCustomers { get; set; } = true;

    /// <summary>Whether the integration is allowed to create missing items/services automatically.</summary>
    public bool AutoCreateItems { get; set; } = true;

    /// <summary>
    /// When true, Sage50Client logs what it *would* create/import instead of actually
    /// calling Save() on the SDK - use this for staged testing (PortPro connectivity,
    /// validation logic) before the first real write to a Sage 50 company file.
    /// </summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// Account numbers that are confirmed to exist in Sage 50 but that
    /// AccountLedger.LoadByAccountNumber/LoadByAccountDisplayString/LoadByAccountName
    /// cannot verify - confirmed live 2026-08-04 specifically for this company's
    /// currency-paired accounts (4100 "Sales Revenue-CDN" / 4110 "Sales Revenue-USD"):
    /// every ordinary single-currency account tested loaded correctly via
    /// LoadByAccountNumber, but these two consistently returned false regardless of
    /// lookup method, format, or call order - a real SDK limitation with
    /// currency-paired accounts, not a code bug we can fix by calling something
    /// differently. AccountExistsAsync treats any account number listed here as
    /// existing without asking the SDK. Only add an account here after confirming
    /// directly in Sage 50 (Setup > Settings > Company > General (Accounts)) that it
    /// genuinely exists - this bypasses real verification for exactly the accounts
    /// listed, nothing else.
    /// </summary>
    public List<string> AccountsUnverifiableBySdk { get; set; } = new();

    /// <summary>
    /// Maps a Canadian tax abbreviation (HST/GST/PST/QST) detected in a PortPro
    /// charge name (e.g. "HST (13 %)") to the corresponding Sage 50 tax code string
    /// from Setup > Settings > Company > Sales Taxes > Tax Codes - e.g. {"HST": "H"}.
    /// Confirmed 2026-08-04 for this company: code "H" = "HST13%", posting to GL
    /// 2310 ("GST Charged on Sales" - shared by both GST and HST in this company's
    /// tax setup).
    ///
    /// A PortPro charge whose detected abbreviation has an entry here is NOT
    /// imported as its own line item/service - InvoiceValidationService resolves it
    /// to this code instead, and Sage50Client applies it to the invoice's other
    /// (revenue) lines via SetTaxCodeString, so Sage 50 calculates and posts the tax
    /// itself rather than trusting PortPro's stated dollar amount (the two models
    /// don't match: PortPro sends tax as an explicit line amount, Sage 50 computes
    /// it from a per-line tax code). Abbreviations with no entry here fall back to
    /// the old warn-and-import-as-item behaviour - safe default for tax types not
    /// yet confirmed against this company's real tax code setup.
    /// </summary>
    public Dictionary<string, string> TaxCodesByAbbreviation { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class SyncSettings
{
    /// <summary>How often the automatic "last changed date" sync runs, in minutes.</summary>
    public int PollingIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// On first run (no watermark stored yet), how many days back to look for changed invoices.
    /// </summary>
    public int InitialLookbackDays { get; set; } = 7;

    /// <summary>Folder the service watches for manual trigger request files dropped by PortProSage.Trigger.</summary>
    public string TriggerFolder { get; set; } = "C:\\PortProSageSync\\requests";

    /// <summary>Folder where processed trigger files and their result reports are archived.</summary>
    public string ProcessedTriggerFolder { get; set; } = "C:\\PortProSageSync\\requests\\processed";

    /// <summary>Path to the local SQLite database used to track sync watermark and imported invoices.</summary>
    public string StateDatabasePath { get; set; } = "C:\\PortProSageSync\\state.db";

    /// <summary>Folder for rolling log files.</summary>
    public string LogFolder { get; set; } = "C:\\PortProSageSync\\logs";

    /// <summary>
    /// Minimum Serilog level: Verbose, Debug, Information, Warning, Error, or Fatal.
    /// Use Debug on the dev server for more detail while testing; Information or
    /// Warning is usually enough once promoted to production.
    /// </summary>
    public string MinimumLogLevel { get; set; } = "Information";
}
