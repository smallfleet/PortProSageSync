using PortProSage.Core.Models;
using PortProSage.Core.PortPro;
using PortProSage.Core.Sage50;

namespace PortProSage.Service;

/// <summary>
/// Standalone connectivity checks, invoked via command-line flag (see Program.cs)
/// so each half of the integration can be validated on its own before wiring them
/// together - e.g. confirm PortPro auth/fetch works even before the Sage 50 SDK
/// is installed, or confirm the Sage 50 SDK connects even with no PortPro
/// credentials configured yet.
/// </summary>
public static class Diagnostics
{
    public static async Task<int> RunAsync(string mode, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        switch (mode.ToLowerInvariant())
        {
            case "portpro":
                return await CheckPortProAsync(services, logger, ct);

            case "sage50":
                return await CheckSage50Async(services, logger, ct);

            default:
                logger.LogError("Unknown --diagnose mode '{Mode}'. Expected 'portpro' or 'sage50'.", mode);
                return 1;
        }
    }

    private static async Task<int> CheckPortProAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("=== STAGE 1: PortPro connectivity check (Sage 50 not touched) ===");
        try
        {
            var client = services.GetRequiredService<PortProClient>();

            // Small, cheap probe: last 24 hours of changed invoices, just to prove
            // auth + the invoices endpoint work end to end.
            var request = new SyncRequest
            {
                FilterType = FilterType.LastChangedDate,
                From = DateTimeOffset.UtcNow.AddDays(-1),
                To = DateTimeOffset.UtcNow,
                RequestedBy = "diagnostic"
            };

            var invoices = await client.GetInvoicesAsync(request, ct);
            logger.LogInformation(
                "SUCCESS: authenticated with PortPro and fetched {Count} invoice(s) changed in the last 24 hours.",
                invoices.Count);

            foreach (var inv in invoices.Take(5))
            {
                logger.LogInformation(
                    "  sample: ref={Ref}, customer={Customer}, total={Total}, updatedAt={Updated}",
                    inv.ReferenceNumber, inv.Caller?.CompanyName ?? inv.CallerName, inv.TotalAmount, inv.UpdatedAt);
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED: could not fetch invoices from PortPro. Check PortPro:AccessToken/RefreshToken " +
                                 "and BaseUrl in appsettings (get these from PortPro's own integration/API settings screen), " +
                                 "and double-check the query parameter names noted in PortProClient.BuildQueryString " +
                                 "against your actual PortPro API reference.");
            return 1;
        }
    }

    private static async Task<int> CheckSage50Async(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("=== STAGE 2: Sage 50 SDK connectivity check (PortPro not touched) ===");
        try
        {
            var sage50 = services.GetRequiredService<ISage50Client>();
            await sage50.ConnectAsync(ct);
            logger.LogInformation("SUCCESS: connected to Sage 50 and opened the configured company file.");

            // A harmless read-only call to confirm the object model actually works,
            // not just that COM instantiation succeeded.
            var probe = await sage50.FindCustomerByNameAsync("__PortProSage_Diagnostic_Probe__", ct);
            logger.LogInformation(
                "Read-only customer lookup completed without error (found: {Found}) - confirms the Customers collection is reachable.",
                probe is not null);

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED: could not connect to Sage 50. Check Sage50:CompanyDataPath/UserName/Password/SdkProgId/" +
                                 "AppId/AppName in appsettings, confirm the SDK is installed and its ProgID matches (see the " +
                                 "startup log line for the detected SDK version), confirm no other exclusive-mode session is " +
                                 "blocking access to the company file, and check Sage 50 itself for an access-grant prompt if " +
                                 "this AppId hasn't connected before.");
            return 1;
        }
    }
}
