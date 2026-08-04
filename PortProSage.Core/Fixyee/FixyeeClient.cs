using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;

namespace PortProSage.Core.Fixyee;

/// <summary>
/// Placeholder for a future Fixyee integration - not called from the sync pipeline
/// yet. Reads FixyeeSettings.ApiKey (see appsettings.json "PortProSage:Fixyee")
/// and attaches it to outgoing requests, mirroring the PortPro auth pattern, so
/// wiring in real endpoints later is mostly filling in the TODOs below rather
/// than restructuring anything.
///
/// To activate this later:
///   1. Set PortProSage:Fixyee:BaseUrl / ApiKey / Enabled in appsettings.json.
///   2. Fill in the real endpoints/DTOs below once you have Fixyee's API docs.
///   3. Register it in Program.cs (builder.Services.AddHttpClient&lt;FixyeeClient&gt;())
///      and either extend SyncOrchestrator to target multiple downstream systems,
///      or stand up a parallel orchestrator for Fixyee if its data shape differs
///      enough from Sage 50's to warrant its own pipeline.
/// </summary>
public class FixyeeClient
{
    private readonly HttpClient _http;
    private readonly FixyeeSettings _settings;
    private readonly ILogger<FixyeeClient> _logger;

    public FixyeeClient(HttpClient http, FixyeeSettings settings, ILogger<FixyeeClient> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_settings.BaseUrl) && _settings.BaseUrl != "REPLACE_ME_WHEN_KNOWN")
        {
            _http.BaseAddress = new Uri(_settings.BaseUrl);
        }
    }

    private void AttachApiKey(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException(
                "Fixyee:ApiKey is not configured. Set PortProSage:Fixyee:ApiKey in appsettings.json before using FixyeeClient.");
        }

        // TODO: confirm Fixyee's actual auth scheme once known - common options are:
        //   - Bearer token:      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        //   - Custom header:     request.Headers.Add("X-Api-Key", _settings.ApiKey);
        //   - Basic auth:        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(...));
        // Bearer is used below as a placeholder default.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
    }

    /// <summary>
    /// Placeholder health-check style call - swap the path for a real Fixyee
    /// endpoint once documented, just to confirm the API key/base URL work.
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Fixyee integration is disabled (PortProSage:Fixyee:Enabled = false); skipping.");
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/TODO-real-health-or-ping-endpoint");
        AttachApiKey(request);

        using var response = await _http.SendAsync(request, ct);
        _logger.LogInformation("Fixyee connectivity check returned {StatusCode}", response.StatusCode);
        return response.IsSuccessStatusCode;
    }
}
