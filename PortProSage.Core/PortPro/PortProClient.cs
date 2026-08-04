using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Models;

namespace PortProSage.Core.PortPro;

public class PortProClient
{
    private readonly HttpClient _http;
    private readonly PortProSettings _settings;
    private readonly PortProAuthService _auth;
    private readonly ILogger<PortProClient> _logger;

    public PortProClient(HttpClient http, PortProSettings settings, PortProAuthService auth, ILogger<PortProClient> logger)
    {
        _http = http;
        _settings = settings;
        _auth = auth;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
    }

    /// <summary>
    /// Fetches every invoice matching the given request, transparently paging
    /// through PortPro's list endpoint via skip/limit.
    ///
    /// PortPro has no confirmed server-side reference-number filter (verified
    /// 2026-08-04 against the production connector's own logs/binary, which
    /// fetches every invoice via skip/limit and filters client-side rather than
    /// asking the API for a range) - so FilterType.InvoiceNumberRange fetches
    /// every page and filters by ReferenceNumber locally.
    /// </summary>
    public async Task<List<PortProInvoice>> GetInvoicesAsync(SyncRequest request, CancellationToken ct)
    {
        var all = new List<PortProInvoice>();
        var skip = 0;

        while (true)
        {
            var query = BuildQueryString(request, skip);
            var url = $"{_settings.BaseUrl}{_settings.InvoiceEndpoint}?{query}";

            _logger.LogInformation("Requesting PortPro invoices skip={Skip} limit={Limit} ({FilterType})", skip, _settings.PageSize, request.FilterType);

            using var response = await SendWithAuthAsync(HttpMethod.Get, url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<PortProInvoiceListResponse>(cancellationToken: ct)
                ?? new PortProInvoiceListResponse();

            // Each "data" entry wraps one load - the actual invoice fields (reference
            // number, pricing, caller, ...) live one level deeper in its "invoice"
            // array. Flatten here so nothing downstream deals with the wrapper.
            var pageInvoices = body.Data.SelectMany(load => load.Invoice.Select(inv =>
            {
                inv.Id = load.Id;
                inv.CreatedAt = load.CreatedAt;
                inv.UpdatedAt = load.UpdatedAt;
                return inv;
            }));

            if (request.FilterType == FilterType.InvoiceNumberRange)
            {
                pageInvoices = pageInvoices.Where(inv => IsInInvoiceNumberRange(inv.ReferenceNumber, request.StartInvoiceNumber, request.EndInvoiceNumber));
            }

            all.AddRange(pageInvoices);

            if (body.Data.Count < _settings.PageSize)
            {
                break; // last page
            }

            skip += _settings.PageSize;
        }

        _logger.LogInformation("Fetched {Count} invoice(s) from PortPro for request {RequestId}", all.Count, request.RequestId);
        return all;
    }

    private static bool IsInInvoiceNumberRange(string referenceNumber, string? start, string? end)
    {
        if (string.IsNullOrEmpty(referenceNumber)) return false;
        if (!string.IsNullOrWhiteSpace(start) && string.CompareOrdinal(referenceNumber, start) < 0) return false;
        if (!string.IsNullOrWhiteSpace(end) && string.CompareOrdinal(referenceNumber, end) > 0) return false;
        return true;
    }

    /// <summary>
    /// Fetches a single invoice by its PortPro reference number - useful for
    /// re-checking an invoice's current state right before import, in case it
    /// changed between the list call and the import step.
    /// </summary>
    public async Task<PortProInvoice?> GetInvoiceAsync(string referenceNumber, CancellationToken ct)
    {
        var url = $"{_settings.BaseUrl}{_settings.InvoiceEndpoint}/{Uri.EscapeDataString(referenceNumber)}";
        using var response = await SendWithAuthAsync(HttpMethod.Get, url, ct);
        if ((int)response.StatusCode == 404) return null;
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<PortProInvoice>(cancellationToken: ct);
    }

    /// <summary>
    /// Sends a request with the current access token attached, and - if PortPro
    /// rejects it with a 401 - refreshes the token once and retries a single time
    /// before giving up. Handles both "our cached expiry estimate was wrong" and
    /// "the token was revoked/rotated externally" cases.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithAuthAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        var response = await SendOnceAsync(method, url, token, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await _auth.NotifyTokenRejectedAsync(ct);
            response = await SendOnceAsync(method, url, token, ct);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, ct);
    }

    // Query parameter names confirmed 2026-08-04 by extracting string literals
    // from the production connector's compiled binary (PostProConnector.exe) and
    // live-testing them against the real API: skip/limit pagination,
    // updatedFrom/updatedTo for last-changed filtering, billingFrom/billingTo for
    // billing-date filtering (the closest analog PortPro exposes to "completed
    // date"). No server-side reference-number filter exists - InvoiceNumberRange
    // is applied client-side in GetInvoicesAsync instead.
    private string BuildQueryString(SyncRequest request, int skip)
    {
        var parts = new List<string>
        {
            $"skip={skip}",
            $"limit={_settings.PageSize}"
        };

        switch (request.FilterType)
        {
            case FilterType.LastChangedDate:
                if (request.From is not null) parts.Add($"updatedFrom={Uri.EscapeDataString(request.From.Value.ToString("O"))}");
                if (request.To is not null) parts.Add($"updatedTo={Uri.EscapeDataString(request.To.Value.ToString("O"))}");
                break;

            case FilterType.CompletedDateRange:
                if (request.From is not null) parts.Add($"billingFrom={Uri.EscapeDataString(request.From.Value.ToString("O"))}");
                if (request.To is not null) parts.Add($"billingTo={Uri.EscapeDataString(request.To.Value.ToString("O"))}");
                break;

            case FilterType.InvoiceNumberRange:
                // No server-side param - see GetInvoicesAsync's client-side filter.
                break;
        }

        return string.Join("&", parts);
    }
}
