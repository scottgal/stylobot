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
    ///     GET an envelope-wrapped payload. Returns <c>default</c> on 404 or on transport /
    ///     deserialisation failure (logged as a warning), so the dashboard degrades to empty
    ///     data when the gateway is unreachable instead of bubbling HTTP 500s to operators.
    /// </summary>
    public async Task<T?> GetEnvelopeAsync<T>(string path, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Remote gateway returned {Status} for GET {Path}", (int)response.StatusCode, path);
                return default;
            }
            var envelope = await response.Content.ReadFromJsonAsync<RemoteEnvelope<T>>(ct);
            return envelope is null ? default : envelope.Data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Remote gateway call failed for GET {Path}", path);
            return default;
        }
    }

    /// <summary>POST an envelope-wrapped payload (used by /api/v1/investigate). Same failure semantics as <see cref="GetEnvelopeAsync"/>.</summary>
    public async Task<TResp?> PostEnvelopeAsync<TReq, TResp>(string path, TReq body, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(path, body, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Remote gateway returned {Status} for POST {Path}", (int)response.StatusCode, path);
                return default;
            }
            var envelope = await response.Content.ReadFromJsonAsync<RemoteEnvelope<TResp>>(ct);
            return envelope is null ? default : envelope.Data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Remote gateway call failed for POST {Path}", path);
            return default;
        }
    }

    /// <summary>
    ///     Convenience wrapper for the common <c>GetEnvelopeAsync&lt;List&lt;T&gt;&gt;</c> +
    ///     <c>?? new List&lt;T&gt;()</c> pattern. Always returns a non-null collection so
    ///     callers don't need to null-coalesce.
    /// </summary>
    public async Task<IReadOnlyList<T>> GetEnvelopeListAsync<T>(string path, CancellationToken ct = default)
    {
        var list = await GetEnvelopeAsync<List<T>>(path, ct);
        return list ?? new List<T>();
    }
}
