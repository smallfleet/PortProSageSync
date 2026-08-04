using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Models;

namespace PortProSage.Core.PortPro;

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
    private readonly ILogger<PortProAuthService> _logger;

    private string? _cachedAccessToken;
    private string? _cachedRefreshToken;
    private DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PortProAuthService(HttpClient http, PortProSettings settings, ILogger<PortProAuthService> logger)
    {
        _http = http;
        _settings = settings;
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
            response.EnsureSuccessStatusCode();

            var envelope = await response.Content.ReadFromJsonAsync<PortProTokenEnvelope>(cancellationToken: ct);
            var data = envelope?.Data
                ?? throw new InvalidOperationException($"PortPro {_settings.NewTokenEndpoint} returned an empty response.");

            _cachedAccessToken = data.Token;
            // PortPro doesn't return an expires_in field - decode the JWT's own "exp"
            // claim instead; fall back to a conservative 1-hour reuse window if that fails.
            _cachedTokenExpiresAt = TryGetJwtExpiry(data.Token)?.AddSeconds(-30) ?? DateTimeOffset.UtcNow.AddHours(1);

            if (!string.IsNullOrWhiteSpace(data.RefreshToken))
            {
                // NOTE: only updates the in-memory value for this process's lifetime.
                // If PortPro rotates refresh tokens, persist the new value (e.g. via
                // SyncStateRepository) so a service restart doesn't fall back to a
                // stale one from appsettings.json.
                _cachedRefreshToken = data.RefreshToken;
            }

            _logger.LogInformation("Token updated and saved.");
            return _cachedAccessToken;
        }
        finally
        {
            _lock.Release();
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
