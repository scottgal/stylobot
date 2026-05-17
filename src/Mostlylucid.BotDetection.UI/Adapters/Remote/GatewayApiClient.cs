using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Thin HTTP wrapper used by every Remote* store. Owns the base URL + API-key header
///     once instead of every store re-implementing it. Helpers always go through the
///     <c>RemoteEnvelope&lt;T&gt;</c> shape because every gateway endpoint wraps its payload
///     in <c>{ "data": ... }</c>.
///
///     This class is registered via <c>AddHttpClient&lt;GatewayApiClient&gt;</c> so the
///     typed-client pattern wires retries / handlers consistently.
/// </summary>
public sealed class GatewayApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GatewayApiClient> _logger;

    public GatewayApiClient(HttpClient http, ILogger<GatewayApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    ///     GET an envelope-wrapped payload. Returns <c>default</c> on 404, throws on other
    ///     non-success status codes so the caller can decide whether to swallow or surface.
    /// </summary>
    public async Task<T?> GetEnvelopeAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<RemoteEnvelope<T>>(ct);
        return envelope is null ? default : envelope.Data;
    }

    /// <summary>POST an envelope-wrapped payload (used by /api/v1/investigate).</summary>
    public async Task<TResp?> PostEnvelopeAsync<TReq, TResp>(string path, TReq body, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(path, body, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<RemoteEnvelope<TResp>>(ct);
        return envelope is null ? default : envelope.Data;
    }
}
