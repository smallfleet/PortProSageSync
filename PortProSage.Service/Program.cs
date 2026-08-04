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
// confirmed live 2026-08-04 that this matters: appsettings.json/appsettings.
// {Environment}.json are looked up relative to ContentRootPath, silently
// contribute nothing if not found there (AddJsonFile's default optional:true),
// and the app falls back to hardcoded C# property defaults with no error. This
// would also break for real under the Service Control Manager, which launches
// services with an unrelated CWD (typically System32), not this app's folder.
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
// This file is per-server, git-ignored (see .gitignore), and applies in every
// environment (Development and Production alike) - each client's install gets
// its own copy created locally when the product is deployed to them; the
// checked-in appsettings*.json files never contain real secret values, only the
// shared/generic structure and defaults. Loaded last so it overrides
// appsettings.json/appsettings.{Environment}.json for anything it sets.
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
        .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName)
        .WriteTo.File(
            Path.Combine(appSettings.Sync.LogFolder, "portpro-sage-sync-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({Environment}) {Message:lj}{NewLine}{Exception}")
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

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation(
    "PortProSageSync starting in {Environment} environment. Sage50 company file: {CompanyPath}. " +
    "PortPro base URL: {PortProUrl}. Trigger folder: {TriggerFolder}.",
    builder.Environment.EnvironmentName,
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
