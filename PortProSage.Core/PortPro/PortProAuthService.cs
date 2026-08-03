using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// PortProSettings.NewTokenEndpoint (e.g. /generate-new-token) with the refresh
/// token to get a new pair - mirroring the "New Tokens Endpoint" field seen in
/// the reference connector tool's settings screen.
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

            var payload = new { refresh_token = _cachedRefreshToken };

            using var response = await _http.PostAsJsonAsync($"{_settings.BaseUrl}{_settings.NewTokenEndpoint}", payload, ct);
            response.EnsureSuccessStatusCode();

            var token = await response.Content.ReadFromJsonAsync<PortProTokenResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException($"PortPro {_settings.NewTokenEndpoint} returned an empty response.");

            _cachedAccessToken = token.AccessToken;
            _cachedTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds > 0 ? token.ExpiresInSeconds - 30 : 3300);

            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                // NOTE: only updates the in-memory value for this process's lifetime.
                // If PortPro rotates refresh tokens, persist the new value (e.g. via
                // SyncStateRepository) so a service restart doesn't fall back to a
                // stale one from appsettings.json.
                _cachedRefreshToken = token.RefreshToken;
            }

            return _cachedAccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }
}
