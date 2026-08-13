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
    /// broadly and filters by ReferenceNumber locally. As of 2026-08-12, that
    /// broad fetch is ALSO bounded by a wide billingFrom/billingTo window (see
    /// BuildQueryString) - a genuinely unfiltered query was confirmed to make
    /// PortPro's API silently omit real invoices that a date-bounded query
    /// correctly returns.
    /// </summary>
    public async Task<List<PortProInvoice>> GetInvoicesAsync(SyncRequest request, CancellationToken ct)
    {
        if (request.FilterType == FilterType.InvoiceNumberList)
        {
            return await GetInvoiceListByReferenceNumbersAsync(request.InvoiceNumberList, ct);
        }

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

    /// <summary>Fetches an explicit, comma-separated set of invoices one at a time
    /// via the single-invoice endpoint (GetInvoiceAsync), NOT the paginated list
    /// endpoint - see FilterType.InvoiceNumberList's doc comment for why this exists
    /// (a confirmed gap where the list endpoint silently omitted a real invoice that
    /// this endpoint returns fine). A reference number not found is logged and
    /// skipped, not treated as fatal - the rest of the list still gets processed.</summary>
    private async Task<List<PortProInvoice>> GetInvoiceListByReferenceNumbersAsync(string? commaSeparated, CancellationToken ct)
    {
        // net48 lacks both StringSplitOptions.TrimEntries and the Split(char, options)
        // overload (both added in later .NET versions) - the classic char[] overload,
        // plus a manual trim, work on every target.
        var referenceNumbers = (commaSeparated ?? string.Empty)
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var found = new List<PortProInvoice>();
        foreach (var referenceNumber in referenceNumbers)
        {
            _logger.LogInformation("Requesting PortPro invoice '{Ref}' directly (InvoiceNumberList)", referenceNumber);
            var invoice = await GetInvoiceAsync(referenceNumber, ct);
            if (invoice is null)
            {
                _logger.LogWarning("Invoice '{Ref}' was not found by PortPro's single-invoice endpoint - skipped.", referenceNumber);
                continue;
            }
            found.Add(invoice);
        }

        _logger.LogInformation("Fetched {Count} of {Requested} requested invoice(s) from PortPro (InvoiceNumberList)", found.Count, referenceNumbers.Count);
        return found;
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

        // See PortProSingleInvoiceResponse's doc comment - this endpoint wraps its
        // response differently than the list endpoint, and one reference number can
        // legitimately span multiple "invoice" entries (one per charge set/load) that
        // need combining into a single result, not returned/treated as duplicates.
        var wrapper = await response.Content.ReadFromJsonAsync<PortProSingleInvoiceResponse>(cancellationToken: ct);
        var chargeSets = wrapper?.Data?.Invoice;
        if (chargeSets is null || chargeSets.Count == 0) return null;

        return ConsolidateChargeSets(wrapper!.Data!, chargeSets);
    }

    /// <summary>Combines every charge set sharing one reference number into a single
    /// PortProInvoice - scalar fields (status/dates/caller) taken from the first
    /// charge set (confirmed identical across charge sets for the same invoice in
    /// the live example that prompted this), pricing lines concatenated and
    /// amount totals summed across all of them.</summary>
    private static PortProInvoice ConsolidateChargeSets(PortProLoadEnvelope envelope, List<PortProInvoice> chargeSets)
    {
        var first = chargeSets[0];
        return new PortProInvoice
        {
            Id = envelope.Id,
            LoadReferenceNumber = first.LoadReferenceNumber,
            ReferenceNumber = first.ReferenceNumber,
            Status = first.Status,
            TotalAmount = chargeSets.Sum(c => c.TotalAmount),
            PaidAmount = chargeSets.Sum(c => c.PaidAmount),
            RemainAmount = chargeSets.Sum(c => c.RemainAmount),
            BillingDate = first.BillingDate,
            CompletedDate = first.CompletedDate,
            CreatedAt = envelope.CreatedAt,
            UpdatedAt = envelope.UpdatedAt,
            Caller = first.Caller,
            CallerName = first.CallerName,
            Pricing = chargeSets.SelectMany(c => c.Pricing).ToList(),
            ReferenceFields = first.ReferenceFields
        };
    }

    /// <summary>Same call as GetInvoiceAsync, but returns the raw response body
    /// untouched - a troubleshooting aid for confirming this endpoint's actual
    /// response shape rather than assuming it matches PortProInvoice's flat
    /// shape (which is really the shape nested one level inside the LIST
    /// endpoint's envelope - see PortProLoadEnvelope's doc comment). Confirmed
    /// live 2026-08-12 this endpoint returns 200 OK with real data for an
    /// invoice GetInvoiceAsync's typed parse came back empty for.</summary>
    public async Task<(int StatusCode, string Body)> GetInvoiceRawAsync(string referenceNumber, CancellationToken ct)
    {
        var url = $"{_settings.BaseUrl}{_settings.InvoiceEndpoint}/{Uri.EscapeDataString(referenceNumber)}";
        using var response = await SendWithAuthAsync(HttpMethod.Get, url, ct);
        var body = await response.Content.ReadAsStringAsync(); // net48's HttpContent has no CancellationToken overload
        return ((int)response.StatusCode, body);
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
                // Confirmed live 2026-08-12: sending NO date filter at all (as this used
                // to) makes PortPro's /invoices endpoint silently omit real, existing
                // invoices that a billingFrom/billingTo-bounded query correctly returns -
                // a specific known invoice (RSRE_000284) never appeared across a full
                // ~3,700-invoice unfiltered scan, but its neighbor (RSRE_000283) appeared
                // immediately once a date-bounded CompletedDateRange query was used
                // instead. There's still no server-side reference-number filter, so this
                // still fetches broadly and filters by ReferenceNumber client-side (see
                // GetInvoicesAsync) - but now within a deliberately wide billingFrom/
                // billingTo window (10 years back to tomorrow) instead of no bound at
                // all, so the API actually returns its complete/correct data set.
                parts.Add($"billingFrom={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddYears(-10).ToString("O"))}");
                parts.Add($"billingTo={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"))}");
                break;

            default:
                // Confirmed live 2026-08-12: falling through here silently (no filter added
                // at all) is exactly what happened when an OLDER Service build received a
                // request written by a NEWER Admin build using a FilterType value the older
                // build's enum didn't define (InvoiceNumberList) - it fetched and started
                // REALLY IMPORTING the entire ~3,700-invoice production account instead of
                // the single invoice actually requested, before being manually stopped.
                // Fail loudly and immediately instead - a version-skew mismatch between
                // whichever exe wrote the request and whichever exe is processing it must
                // never be allowed to silently mean "process everything for real".
                throw new InvalidOperationException(
                    $"Unrecognized SyncRequest.FilterType value {(int)request.FilterType} ('{request.FilterType}') - " +
                    "refusing to fetch invoices without a known filter. This usually means the request was written " +
                    "by a newer build than the one processing it (e.g. the Admin app and the Service .exe are out of " +
                    "sync) - rebuild/redeploy so both understand the same FilterType values.");
        }

        return string.Join("&", parts);
    }
}
