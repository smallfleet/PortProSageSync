using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Data;
using PortProSage.Core.Fixyee;
using PortProSage.Core.Notifications;
using PortProSage.Core.PortPro;
using PortProSage.Core.Sage50;
using PortProSage.Core.Sync;
using PortProSage.Core.Validation;
using PortProSage.Service;
using Serilog;

// ContentRootPath must be pinned to the executable's own directory, not the
// process's current working directory (Host.CreateApplicationBuilder's default) -
// confirmed live 2026-08-04 that this matters: appsettings.json is looked up
// relative to ContentRootPath, silently contributes nothing if not found there
// (AddJsonFile's default optional:true), and the app falls back to hardcoded C#
// property defaults with no error. This would also break for real under the
// Service Control Manager, which launches services with an unrelated CWD
// (typically System32), not this app's folder.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Run as a native Windows Service when launched by the Service Control Manager;
// behaves as a normal console app when run interactively (e.g. for local testing).
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PortProSageSync";
});

// appsettings.Local.json holds this specific install's real secrets (PortPro
// AccessToken/RefreshToken, Sage50 Password, and anything else that varies per
// client deployment) as plain JSON - not dotnet user-secrets (dev-machine-profile
// only, invisible/inconvenient on a client's server) and not environment
// variables (has to be re-set per machine, doesn't travel with the deployment).
// This file is per-server, git-ignored (see .gitignore) - each client's install
// gets its own copy created locally when the product is deployed to them; the
// checked-in appsettings.json never contains real secret values, only the
// shared/generic structure and defaults. Loaded last so it overrides
// appsettings.json for anything it sets.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var appSettings = new AppSettings();
builder.Configuration.GetSection("PortProSage").Bind(appSettings);

builder.Services.AddSingleton(appSettings);
builder.Services.AddSingleton(appSettings.PortPro);
builder.Services.AddSingleton(appSettings.Sage50);
builder.Services.AddSingleton(appSettings.Sync);
builder.Services.AddSingleton(appSettings.Fixyee); // placeholder for the future Fixyee integration
builder.Services.AddSingleton(appSettings.Email);

Directory.CreateDirectory(appSettings.Sync.LogFolder);

var logLevel = Enum.TryParse<Serilog.Events.LogEventLevel>(appSettings.Sync.MinimumLogLevel, ignoreCase: true, out var parsedLevel)
    ? parsedLevel
    : Serilog.Events.LogEventLevel.Information;

builder.Services.AddSerilog((services, loggerConfig) =>
{
    loggerConfig
        .MinimumLevel.Is(logLevel)
        .WriteTo.File(
            Path.Combine(appSettings.Sync.LogFolder, "portpro-sage-sync-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.Console();
});

builder.Services.AddHttpClient<PortProAuthService>();
builder.Services.AddHttpClient<PortProClient>();
builder.Services.AddHttpClient<FixyeeClient>(); // placeholder - not called anywhere yet

builder.Services.AddSingleton<SyncStateRepository>();
builder.Services.AddSingleton<ISage50Client, Sage50Client>();
builder.Services.AddSingleton<InvoiceValidationService>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<SyncOrchestrator>();

builder.Services.AddHostedService<Worker>();

// MUST be disposed before the process exits, on every code path below (including
// every early "return exitCode" from the one-off command branches) - ISage50Client
// is a DI singleton, and its Dispose() is what calls SDKInstanceManager.Instance.
// CloseDatabase(). Confirmed live 2026-08-04: every one-off command (--set-anchor,
// --real-transfer, --create-test-item) used to just "return exitCode" directly
// without ever disposing the host, so CloseDatabase() was never called - real
// writes appeared to succeed (Post()/Save() returned true, no exception) but were
// never durably committed to the actual company file. host.Run()'s long-running
// path already disposes internally on graceful shutdown; this "using" doesn't
// change that; it only fixes the one-off command paths, which is where the
// disposal was actually missing.
using var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation(
    "PortProSageSync starting. Sage50 company file: {CompanyPath}. " +
    "PortPro base URL: {PortProUrl}. Trigger folder: {TriggerFolder}.",
    appSettings.Sage50.CompanyDataPath,
    appSettings.PortPro.BaseUrl,
    appSettings.Sync.TriggerFolder);

// --diagnose portpro | --diagnose sage50 : run a single isolated connectivity check
// and exit, without starting the Worker's polling loop. See README "Staged testing".
var diagnoseIndex = Array.IndexOf(args, "--diagnose");
if (diagnoseIndex >= 0 && diagnoseIndex + 1 < args.Length)
{
    using var scope = host.Services.CreateScope();
    var exitCode = await Diagnostics.RunAsync(args[diagnoseIndex + 1], scope.ServiceProvider, startupLogger, CancellationToken.None);
    return exitCode;
}

// --run-once <RequestJsonPath> : execute exactly one SyncRequest (read from the
// given file) and exit - never starts the Worker's polling loop. This is the
// "Manual Run" path from the Admin app: runs immediately in this dedicated
// process regardless of whether an automatic Service instance is running
// elsewhere, and the caller can track/kill this specific process. See
// Diagnostics.RunOnceAsync.
var runOnceIndex = Array.IndexOf(args, "--run-once");
if (runOnceIndex >= 0 && runOnceIndex + 1 < args.Length)
{
    using var scope = host.Services.CreateScope();
    var exitCode = await Diagnostics.RunOnceAsync(args[runOnceIndex + 1], scope.ServiceProvider, startupLogger, CancellationToken.None);
    return exitCode;
}

// --set-anchor <ReferenceNumber> : manually correct the persisted "continue from
// where we left off" watermark to a specific invoice, then exit. PortPro-read-only,
// never touches Sage 50 - see Diagnostics.SetAnchorAsync.
var setAnchorIndex = Array.IndexOf(args, "--set-anchor");
if (setAnchorIndex >= 0 && setAnchorIndex + 1 < args.Length)
{
    using var scope = host.Services.CreateScope();
    var exitCode = await Diagnostics.SetAnchorAsync(args[setAnchorIndex + 1], scope.ServiceProvider, startupLogger, CancellationToken.None);
    return exitCode;
}

// --real-transfer <AfterInvoiceNumber> <Count> : real (non-DryRun) write of up to
// Count invoices strictly after AfterInvoiceNumber, then exit. Never starts the
// Worker's automatic-poll loop - see Diagnostics.RealTransferAsync.
var realTransferIndex = Array.IndexOf(args, "--real-transfer");
if (realTransferIndex >= 0 && realTransferIndex + 2 < args.Length)
{
    using var scope = host.Services.CreateScope();
    var afterInvoiceNumber = args[realTransferIndex + 1];
    if (!int.TryParse(args[realTransferIndex + 2], out var count) || count <= 0)
    {
        startupLogger.LogError("Invalid --real-transfer count '{Count}' - must be a positive integer.", args[realTransferIndex + 2]);
        return 1;
    }
    var exitCode = await Diagnostics.RealTransferAsync(afterInvoiceNumber, count, scope.ServiceProvider, startupLogger, CancellationToken.None);
    return exitCode;
}

// --imported-range : report the full range/list of everything recorded as
// imported so far, then exit. Local state only - see Diagnostics.ReportImportedRange.
if (Array.IndexOf(args, "--imported-range") >= 0)
{
    using var scope = host.Services.CreateScope();
    return Diagnostics.ReportImportedRange(scope.ServiceProvider, startupLogger);
}

// --clear-false-imports <StartRef> <EndRef> : remove false-positive
// imported_invoice records for a reference-number range, then exit. Local state
// correction only - PortPro/Sage 50 not touched. See Diagnostics.ClearFalseImports.
var clearFalseImportsIndex = Array.IndexOf(args, "--clear-false-imports");
if (clearFalseImportsIndex >= 0 && clearFalseImportsIndex + 2 < args.Length)
{
    using var scope = host.Services.CreateScope();
    var exitCode = Diagnostics.ClearFalseImports(args[clearFalseImportsIndex + 1], args[clearFalseImportsIndex + 2], scope.ServiceProvider, startupLogger);
    return exitCode;
}

// --mark-imported-range <StartRef> <EndRef> : mark every invoice in a reference-
// number range as imported (using its own reference as the Sage 50 number), then
// exit. PortPro read-only; Sage 50 not touched. See Diagnostics.MarkImportedRangeAsync.
var markImportedRangeIndex = Array.IndexOf(args, "--mark-imported-range");
if (markImportedRangeIndex >= 0 && markImportedRangeIndex + 2 < args.Length)
{
    using var scope = host.Services.CreateScope();
    var exitCode = await Diagnostics.MarkImportedRangeAsync(args[markImportedRangeIndex + 1], args[markImportedRangeIndex + 2], scope.ServiceProvider, startupLogger, CancellationToken.None);
    return exitCode;
}

// --create-test-item <Code> <RevenueAccount> : isolated real write of one
// throwaway service item, then exit. See Diagnostics.CreateTestItemAsync.
var createTestItemIndex = Array.IndexOf(args, "--create-test-item");
if (createTestItemIndex >= 0 && createTestItemIndex + 2 < args.Length)
{
    using var scope = host.Services.CreateScope();
    var exitCode = await Diagnostics.CreateTestItemAsync(args[createTestItemIndex + 1], args[createTestItemIndex + 2], scope.ServiceProvider, startupLogger, CancellationToken.None);
    return exitCode;
}

host.Run();
return 0;
