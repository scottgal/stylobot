namespace Stylobot.Gateway.Services;

public record ProfileRequestSnapshot
{
    public required string RequestId { get; init; }
    public required string ClientIp { get; init; }
    public required string UserAgent { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required Dictionary<string, string[]> Headers { get; init; }
    public string? TlsProtocol { get; init; }
    public string? TlsCipherSuite { get; init; }
    public required DateTime CapturedAt { get; init; }

    public static ProfileRequestSnapshot From(HttpContext ctx)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in ctx.Request.Headers)
            headers[key] = values.ToArray()!;

        return new ProfileRequestSnapshot
        {
            RequestId = ctx.TraceIdentifier,
            ClientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = ctx.Request.Headers.UserAgent.ToString(),
            Method = ctx.Request.Method,
            Path = ctx.Request.Path.Value ?? "/",
            Headers = headers,
            TlsProtocol = ctx.Items.TryGetValue("TLS.Protocol", out var p) ? p?.ToString() : null,
            TlsCipherSuite = ctx.Items.TryGetValue("TLS.CipherSuite", out var c) ? c?.ToString() : null,
            CapturedAt = DateTime.UtcNow,
        };
    }
}
