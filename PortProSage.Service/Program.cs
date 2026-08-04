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

host.Run();
return 0;
