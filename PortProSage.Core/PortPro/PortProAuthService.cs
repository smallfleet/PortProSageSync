using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Models;

namespace PortProSage.Core.PortPro;

/// <summary>Shared, genuinely-singleton holder for "PortPro's refresh token has
/// been confirmed rejected" - deliberately a separate tiny class, registered with
/// its own AddSingleton, rather than a private field on PortProAuthService itself.
/// PortProAuthService is registered via AddHttpClient (transient client instance
/// per resolution, by design, so each consumer gets its own properly-pooled
/// HttpClient) - SyncOrchestrator and CustomerSyncService are separate singletons
/// that each inject their own PortProClient/PortProAuthService, so a plain
/// instance field on PortProAuthService would NOT be shared between them.
/// Confirmed live 2026-08-25: within one Manual Run, three independent
/// operations (the customer-change preview, the gap-fill sweep, the trailing
/// customer sync sweep) each separately hit PortPro's real 401-on-refresh (the
/// refresh token itself rejected, not just the access token) and each logged a
/// full duplicate multi-frame stack trace for what was already a known-dead
/// credential by the time the second and third ran.</summary>
public class PortProAuthCircuitState
{
    public Exception? RefreshTokenConfirmedDead { get; set; }
}

/// <summary>
/// Handles PortPro token refresh.
///
/// This account's PortPro setup issues an Access Token + Refresh Token pair
/// directly (no client id/secret exchange) - typically generated once from
/// within PortPro's own integration/API settings screen, or provided by your
/// PortPro account rep. Paste the current pair into PortProSettings.AccessToken /
/// RefreshToken to start. From then on, this service uses the access token as
/// a Bearer token until it's rejected or nears expiry, then calls
/// PortProSettings.NewTokenEndpoint (e.g. /generate-new-token) to get a new pair.
///
/// Confirmed live 2026-08-04 (by testing against the real API with the
/// production connector's own working credentials): this is a GET request with
/// the refresh token itself sent as the Bearer credential, not a POST with a
/// JSON body - PortPro returns 404 for POST on this route.
/// </summary>
public class PortProAuthService
{
    private readonly HttpClient _http;
    private readonly PortProSettings _settings;
    private readonly PortProAuthCircuitState _circuitState;
    private readonly ILogger<PortProAuthService> _logger;

    private string? _cachedAccessToken;
    private string? _cachedRefreshToken;
    private DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PortProAuthService(HttpClient http, PortProSettings settings, PortProAuthCircuitState circuitState, ILogger<PortProAuthService> logger)
    {
        _http = http;
        _settings = settings;
        _circuitState = circuitState;
        _logger = logger;

        _cachedAccessToken = string.IsNullOrWhiteSpace(settings.AccessToken) ? null : settings.AccessToken;
        _cachedRefreshToken = settings.RefreshToken;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // We don't know this access token's real expiry up front (PortPro didn't
        // hand us one via config), so we optimistically reuse it until a caller
        // tells us it was rejected via NotifyTokenRejectedAsync.
        if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _cachedTokenExpiresAt)
        {
            return _cachedAccessToken;
        }

        return await RefreshAsync(ct);
    }

    /// <summary>
    /// Call this after a PortPro request comes back 401, so the next call gets a
    /// freshly refreshed token instead of retrying with the same rejected one.
    /// </summary>
    public async Task<string> NotifyTokenRejectedAsync(CancellationToken ct)
    {
        _logger.LogWarning("PortPro rejected the current access token - refreshing.");
        return await RefreshAsync(ct);
    }

    private async Task<string> RefreshAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Fail fast, not fail-again - see PortProAuthCircuitState's doc
            // comment. The first rejection (below) is still logged in full for
            // diagnosis; everything after that in this process just re-throws
            // this same cached failure instead of repeating the doomed round-trip.
            if (_circuitState.RefreshTokenConfirmedDead is { } dead)
            {
                throw new InvalidOperationException(
                    "PortPro's refresh token was already confirmed rejected earlier in this run - not retrying " +
                    "the same doomed refresh call again. A new PortPro:AccessToken/RefreshToken pair is needed " +
                    "(from PortPro's integration/API settings), then restart the Service or start a new Manual Run.",
                    dead);
            }

            if (string.IsNullOrWhiteSpace(_cachedRefreshToken))
            {
                if (!string.IsNullOrWhiteSpace(_cachedAccessToken))
                {
                    // No refresh token configured yet - fall back to the static
                    // access token from config for as long as it keeps working.
                    _logger.LogWarning(
                        "No PortPro refresh token configured; reusing the static access token from " +
                        "PortPro:AccessToken. Set PortPro:RefreshToken once you have one so the service " +
                        "can renew it automatically instead of failing when it expires.");
                    return _cachedAccessToken!;
                }

                throw new InvalidOperationException(
                    "No PortPro AccessToken or RefreshToken configured. Paste the token pair from PortPro's " +
                    "integration/API settings into PortPro:AccessToken / PortPro:RefreshToken in appsettings.json.");
            }

            _logger.LogInformation("Requesting a fresh PortPro access token via {Endpoint}", _settings.NewTokenEndpoint);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_settings.BaseUrl}{_settings.NewTokenEndpoint}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedRefreshToken);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // The refresh token itself was rejected, not just the access
                // token - nothing left to retry with. Latch this (shared via
                // PortProAuthCircuitState) so every later call in this process,
                // from any consumer, fails fast instead of repeating the same
                // doomed round-trip and logging another duplicate stack trace.
                var deadEx = new InvalidOperationException(
                    $"PortPro rejected the refresh token itself (401 on {_settings.NewTokenEndpoint}) - the " +
                    "configured PortPro:RefreshToken is expired or invalid. A new token pair is needed from " +
                    "PortPro's integration/API settings.");
                _circuitState.RefreshTokenConfirmedDead = deadEx;
                throw deadEx;
            }
            response.EnsureSuccessStatusCode();

            var envelope = await response.Content.ReadFromJsonAsync<PortProTokenEnvelope>(cancellationToken: ct);
            var data = envelope?.Data
                ?? throw new InvalidOperationException($"PortPro {_settings.NewTokenEndpoint} returned an empty response.");

            _cachedAccessToken = data.Token;
            // PortPro doesn't return an expires_in field - decode the JWT's own "exp"
            // claim instead; fall back to a conservative 1-hour reuse window if that fails.
            _cachedTokenExpiresAt = TryGetJwtExpiry(data.Token)?.AddSeconds(-30) ?? DateTimeOffset.UtcNow.AddHours(1);

            var refreshTokenRotated = !string.IsNullOrWhiteSpace(data.RefreshToken)
                && !string.Equals(data.RefreshToken, _cachedRefreshToken, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(data.RefreshToken))
            {
                _cachedRefreshToken = data.RefreshToken;
            }

            // Confirmed live 2026-08-25: PortPro rotates the refresh token on every
            // real use, but this was previously only ever kept in memory - a fresh
            // process (every Manual Run gets its own) re-read the OLD, already-
            // rotated-away token from disk and got an immediate 401 on its very
            // first refresh, even though the token PortPro actually wants was
            // issued only moments earlier by a prior run. Persisted here so the
            // NEXT process, not just this one, has the current pair.
            if (refreshTokenRotated)
            {
                PersistCurrentTokensToLocalConfig();
            }

            _logger.LogInformation("Token updated and saved.");
            return _cachedAccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Writes the current in-memory access/refresh token pair back to
    /// THIS process's own appsettings.Local.json (resolved the same way Program.cs
    /// resolves it - relative to AppContext.BaseDirectory, so this always targets
    /// whichever Debug/Release copy is actually running, never a different one).
    /// Best-effort by design: called only from inside RefreshAsync's lock, and any
    /// failure here is logged and swallowed, never thrown - a failed write leaves
    /// the in-memory tokens (already updated by the caller) working fine for the
    /// rest of THIS run either way; only a FUTURE process would be affected, and
    /// that's already the status quo this method is trying to improve on, not a
    /// regression.</summary>
    private void PersistCurrentTokensToLocalConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
        try
        {
            var existingText = File.Exists(path) ? File.ReadAllText(path) : "{}";
            var root = JsonNode.Parse(existingText) as JsonObject ?? new JsonObject();

            var portProSage = root["PortProSage"] as JsonObject;
            if (portProSage is null)
            {
                portProSage = new JsonObject();
                root["PortProSage"] = portProSage;
            }

            var portPro = portProSage["PortPro"] as JsonObject;
            if (portPro is null)
            {
                portPro = new JsonObject();
                portProSage["PortPro"] = portPro;
            }

            portPro["AccessToken"] = _cachedAccessToken;
            portPro["RefreshToken"] = _cachedRefreshToken;

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            WriteTextWithRetry(path, json);
            _logger.LogInformation("Persisted PortPro's rotated refresh token to {Path} so future runs pick it up instead of a stale one.", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not persist PortPro's rotated refresh token to {Path} - it will keep working for the rest " +
                "of THIS run, but the next process may read a now-stale token back from disk and fail on it.", path);
        }
    }

    /// <summary>Same retry-on-transient-file-conflict pattern used for result.json
    /// checkpoints (Diagnostics.WriteResultFileWithRetry) - this file could
    /// momentarily be open elsewhere (e.g. an operator editing it, or another
    /// process's own concurrent token refresh).</summary>
    private static void WriteTextWithRetry(string path, string content)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream);
                writer.Write(content);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static DateTimeOffset? TryGetJwtExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expUnix))
            {
                return DateTimeOffset.FromUnixTimeSeconds(expUnix);
            }
        }
        catch (Exception)
        {
            // Malformed/unexpected JWT shape - caller falls back to a conservative default.
        }

        return null;
    }
}
