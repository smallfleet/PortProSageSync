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

    /// <summary>
    /// Failure-notification email - not wired up with real credentials yet, same
    /// placeholder pattern as PortPro/Sage50 secrets. See EmailSettings.
    /// </summary>
    public EmailSettings Email { get; set; } = new();
}

/// <summary>
/// SMTP settings for emailing a CSV of failed transactions after any sync run
/// (automatic or manual) that has at least one failure - see
/// FailedTransactionReport and SyncOrchestrator. All placeholder/blank by
/// default; fill in via user-secrets/environment variables
/// (PortProSage__Email__Password, etc.), same as PortPro/Sage50 secrets - don't
/// put real credentials in the committed appsettings.json.
/// </summary>
public class EmailSettings
{
    /// <summary>SMTP server hostname, e.g. smtp.office365.com.</summary>
    public string SmtpHost { get; set; } = string.Empty;

    /// <summary>SMTP server port, e.g. 587 for STARTTLS.</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>Whether to use SSL/TLS for the SMTP connection.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>"From" address on the notification email.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>SMTP authentication username (often the same as FromAddress).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>SMTP authentication password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated list of recipient addresses for failed-transaction
    /// notifications, e.g. "ops@example.com,accounting@example.com".
    /// </summary>
    public string RecipientAddressesCsv { get; set; } = string.Empty;

    /// <summary>Set to true once SmtpHost/FromAddress/credentials are filled in for real.</summary>
    public bool Enabled { get; set; } = false;
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

    /// <summary>
    /// Optional charge-to-account lookup table, matched against each PortPro
    /// pricing line's charge name (case-insensitive). A charge with no matching
    /// row here, or a row whose Sage50AccountNumber is blank, falls back to
    /// DefaultRevenueAccount - PortPro's own glCode is NOT consulted (confirmed
    /// live 2026-08-05 this was a real bug: this company's "4020"-coded charges
    /// don't have a matching 4020 account in Sage 50 at all, so using glCode as an
    /// intermediate fallback caused every one of those charges to fail validation
    /// instead of correctly falling through to the default).
    /// PortProChargeNumber/Sage50AccountName are for readability/audit only - the
    /// row is matched on PortProChargeName, and only Sage50AccountNumber (when
    /// non-blank) actually changes what gets posted. See
    /// InvoiceValidationService.TryResolveAccountForCharge for the exact
    /// resolution order: this table's Sage50AccountNumber (if set) >
    /// DefaultRevenueAccount > error (process stops) if neither resolves.
    /// </summary>
    public List<ChargeAccountMapping> ChargeAccountMap { get; set; } = new();

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

/// <summary>One row of Sage50Settings.ChargeAccountMap - see that property's doc comment.</summary>
public class ChargeAccountMapping
{
    /// <summary>PortPro's charge name (e.g. "PREPULL") - matched against each pricing line's Name.</summary>
    public string PortProChargeName { get; set; } = string.Empty;

    /// <summary>PortPro's own glCode for this charge. Reference/documentation only - not used to resolve the account.</summary>
    public string PortProChargeNumber { get; set; } = string.Empty;

    /// <summary>Sage 50 account name, for reference/documentation only.</summary>
    public string Sage50AccountName { get; set; } = string.Empty;

    /// <summary>Sage 50 GL account number to actually post this charge to. Leave blank to fall back to Sage50Settings.DefaultRevenueAccount.</summary>
    public string Sage50AccountNumber { get; set; } = string.Empty;
}

public class SyncSettings
{
    /// <summary>
    /// If set, no invoice dated before this is ever processed by ANY run - manual
    /// or automatic, regardless of mode. Checked in SyncOrchestrator.RunAsync
    /// against each invoice's own date (BillingDate, falling back to
    /// CompletedDate) before it's ever handed to Sage 50, the same way the zero/
    /// negative-amount filter already works. This exists specifically to catch,
    /// in our own code, the exact situation that has repeatedly killed a run
    /// outright: Sage 50's own "Do Not Allow Transactions Dated Before" company
    /// setting rejects a too-old invoice mid-write, which
    /// Sage50Client.TerminateOnFatalWriteError treats as unrecoverable and
    /// terminates the whole process over. Filtering it out here instead turns that
    /// into an ordinary, logged skip - nothing crashes, and every other eligible
    /// invoice in the same run still gets processed. Null (the default) disables
    /// this check entirely - every invoice is eligible regardless of date.
    /// </summary>
    public DateTimeOffset? CutoffInvoiceDate { get; set; }

    /// <summary>How often the automatic "last changed date" sync runs, in minutes.</summary>
    public int PollingIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Serves two roles under one number, both driven by the same "hold back N
    /// days" idea:
    ///   1. Every watermark-driven run (the automatic poll, or a manual "Continue"
    ///      run) caps its upper date bound at (now - this many days), not raw
    ///      "now" - e.g. if today is Aug 9 and this is 7, nothing dated after
    ///      Aug 2 is processed yet. This gives a recently-changed invoice time to
    ///      settle/be corrected in PortPro before it's ever synced to Sage 50.
    ///      Nothing is permanently skipped - the persisted watermark never
    ///      advances past this capped bound either, so a held-back invoice is
    ///      simply picked up on a later run once it ages past the delay window.
    ///   2. On the very first run ever (no watermark saved yet), it also sets how
    ///      far back that first run's LOWER bound starts - e.g. 7 means the very
    ///      first run covers invoices from 7 days before the (already-delayed)
    ///      upper bound above.
    /// </summary>
    public int ProcessingDelayDays { get; set; } = 7;

    /// <summary>
    /// When true (the default), a run whose resolved invoice-date window
    /// (SyncResult.EffectiveFromUtc..EffectiveToUtc) spans more than one day is
    /// split into a sequence of single-day batches, each fetched, processed, and
    /// committed (its own persisted-watermark advance) in turn - so if the
    /// process dies partway through a wide window (e.g. a first-ever run
    /// covering weeks of backlog), whatever days already completed stay durably
    /// committed instead of the whole window having to be retried from scratch.
    /// Every batch - including day 1 of an automatic poll's normal few-hour
    /// window - is logged individually ("Batch N of M"), and a run that only
    /// ever produces one batch (either because SplitRunByDay is false, or the
    /// window is a day or narrower to begin with) still logs that single batch
    /// the same way, so the log format never depends on how many batches there
    /// turned out to be. False processes the entire resolved window as one
    /// batch, exactly as this always worked before this setting existed.
    /// Applies to Manual Run and the Automatic Service alike; has no effect on
    /// Invoice number range mode, which has no date window to split.
    /// </summary>
    public bool SplitRunByDay { get; set; } = true;

    /// <summary>Folder the service watches for manual trigger request files dropped by PortProSage.Trigger.</summary>
    public string TriggerFolder { get; set; } = "C:\\PortProSageSync\\requests";

    /// <summary>Folder where processed trigger files and their result reports are archived.</summary>
    public string ProcessedTriggerFolder { get; set; } = "C:\\PortProSageSync\\requests\\processed";

    /// <summary>Path to the local SQLite database used to track sync watermark and imported invoices.</summary>
    public string StateDatabasePath { get; set; } = "C:\\PortProSageSync\\state.db";

    /// <summary>Folder for rolling log files.</summary>
    public string LogFolder { get; set; } = "C:\\PortProSageSync\\logs";

    /// <summary>
    /// Folder for per-run CSV files listing failed transactions (see
    /// FailedTransactionReport) - one file per sync run that had at least one
    /// failure, named with a microsecond-precision timestamp so concurrent/rapid
    /// runs never collide.
    /// </summary>
    public string FailedTransactionsFolder { get; set; } = "C:\\PortProSageSync\\failed-transactions";

    /// <summary>
    /// Minimum Serilog level: Verbose, Debug, Information, Warning, Error, or Fatal.
    /// Use Debug on the dev server for more detail while testing; Information or
    /// Warning is usually enough once promoted to production.
    /// </summary>
    public string MinimumLogLevel { get; set; } = "Information";

    /// <summary>
    /// How many days of daily rolling log files (LogFolder) to keep - checked at
    /// the end of every sync run (see LogRetentionService), not on a separate
    /// timer, so it's tied to actual activity rather than needing its own
    /// scheduling. A file whose date in its name (portpro-sage-sync-yyyyMMdd.log)
    /// is older than today minus this many days is deleted permanently - this is
    /// NOT recoverable. 0 disables cleanup entirely (the default posture: never
    /// delete anything unless explicitly configured to).
    /// </summary>
    public int LogRetentionDays { get; set; } = 0;
}
