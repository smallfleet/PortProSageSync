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
    /// through PortPro's list endpoint using the configured page size.
    /// </summary>
    public async Task<List<PortProInvoice>> GetInvoicesAsync(SyncRequest request, CancellationToken ct)
    {
        var all = new List<PortProInvoice>();
        var page = 1;

        while (true)
        {
            var query = BuildQueryString(request, page);
            var url = $"{_settings.BaseUrl}{_settings.InvoiceEndpoint}?{query}";

            _logger.LogInformation("Requesting PortPro invoices page {Page} ({FilterType})", page, request.FilterType);

            using var response = await SendWithAuthAsync(HttpMethod.Get, url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<PortProInvoiceListResponse>(cancellationToken: ct)
                ?? new PortProInvoiceListResponse();

            all.AddRange(body.Invoice);

            if (body.Invoice.Count < _settings.PageSize)
            {
                break; // last page
            }

            page++;
        }

        _logger.LogInformation("Fetched {Count} invoice(s) from PortPro for request {RequestId}", all.Count, request.RequestId);
        return all;
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

    // NOTE: query parameter names below (updatedAtFrom/To, completedDateFrom/To,
    // referenceNumberFrom/To) are still a best-guess convention - the reference
    // connector's settings screen confirmed the base URL and token endpoints, but
    // not the exact filter parameter names for GET /invoices. Confirm these against
    // your PortPro API reference or a Postman test before relying on them.
    private string BuildQueryString(SyncRequest request, int page)
    {
        var parts = new List<string>
        {
            $"page={page}",
            $"limit={_settings.PageSize}"
        };

        switch (request.FilterType)
        {
            case FilterType.LastChangedDate:
                if (request.From is not null) parts.Add($"updatedAtFrom={Uri.EscapeDataString(request.From.Value.ToString("O"))}");
                if (request.To is not null) parts.Add($"updatedAtTo={Uri.EscapeDataString(request.To.Value.ToString("O"))}");
                break;

            case FilterType.CompletedDateRange:
                if (request.From is not null) parts.Add($"completedDateFrom={Uri.EscapeDataString(request.From.Value.ToString("O"))}");
                if (request.To is not null) parts.Add($"completedDateTo={Uri.EscapeDataString(request.To.Value.ToString("O"))}");
                break;

            case FilterType.InvoiceNumberRange:
                if (!string.IsNullOrWhiteSpace(request.StartInvoiceNumber)) parts.Add($"referenceNumberFrom={Uri.EscapeDataString(request.StartInvoiceNumber)}");
                if (!string.IsNullOrWhiteSpace(request.EndInvoiceNumber)) parts.Add($"referenceNumberTo={Uri.EscapeDataString(request.EndInvoiceNumber)}");
                break;
        }

        return string.Join("&", parts);
    }
}
