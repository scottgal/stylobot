using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.OpenApi;

/// <summary>
///     Loads OpenAPI 3.x JSON documents from URL or file path. Uses System.Text.Json
///     directly (no Microsoft.OpenApi.Readers dep) so it stays AOT-friendly and avoids
///     version conflicts with the rest of the .NET 10 toolchain. YAML and external
///     $ref resolution are out of scope for v1.
/// </summary>
public sealed class OpenApiDocumentLoader(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenApiDocumentLoader> logger)
    : IOpenApiDocumentLoader
{
    private readonly ConcurrentDictionary<string, LoadedOpenApiDocument> _cache = new();

    public async Task<LoadedOpenApiDocument?> LoadAsync(
        string source, bool continueOnFailure = false, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(source, out var cached)) return cached;

        try
        {
            var rawJson = await ReadAsStringAsync(source, ct);
            using var json = JsonDocument.Parse(rawJson);
            var root = json.RootElement;

            var info = root.TryGetProperty("info", out var infoEl) ? infoEl : default;
            var title = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("title", out var t)
                ? t.GetString() : null;
            var version = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("version", out var v)
                ? v.GetString() : null;

            var operations = ExtractOperations(root).ToList();

            var doc = new LoadedOpenApiDocument(
                Source: source,
                Title: title,
                Version: version,
                Operations: operations,
                LoadedUtc: DateTime.UtcNow,
                RawJson: rawJson);

            _cache[source] = doc;
            logger.LogInformation(
                "Loaded OpenAPI spec from {Source}: {Title} v{Version} ({OpCount} operations)",
                source, title ?? "unknown", version ?? "unknown", operations.Count);
            return doc;
        }
        catch (Exception ex) when (continueOnFailure)
        {
            logger.LogWarning(ex, "Failed to load OpenAPI spec from {Source}; continuing", source);
            return null;
        }
    }

    public void ClearCache() => _cache.Clear();

    public bool Invalidate(string source) => _cache.TryRemove(source, out _);

    private async Task<string> ReadAsStringAsync(string source, CancellationToken ct)
    {
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var http = httpClientFactory.CreateClient("stylobot-openapi");
            var response = await http.GetAsync(source, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }

        if (!File.Exists(source))
            throw new FileNotFoundException($"OpenAPI spec file not found: {source}", source);
        return await File.ReadAllTextAsync(source, ct);
    }

    private static readonly string[] HttpMethods =
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace"
    };

    private static IEnumerable<LoadedOpenApiOperation> ExtractOperations(JsonElement root)
    {
        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var pathProp in paths.EnumerateObject())
        {
            var pathTemplate = pathProp.Name;
            if (pathProp.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var methodProp in pathProp.Value.EnumerateObject())
            {
                var method = methodProp.Name.ToLowerInvariant();
                if (Array.IndexOf(HttpMethods, method) < 0) continue;
                if (methodProp.Value.ValueKind != JsonValueKind.Object) continue;

                yield return ParseOperation(pathTemplate, method, methodProp.Value);
            }
        }
    }

    private static LoadedOpenApiOperation ParseOperation(string path, string method, JsonElement op)
    {
        var operationId = TryGetString(op, "operationId");
        var summary     = TryGetString(op, "summary");
        var description = TryGetString(op, "description");
        var deprecated  = op.TryGetProperty("deprecated", out var depEl)
                          && depEl.ValueKind == JsonValueKind.True;

        var tags = new List<string>();
        if (op.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tagsEl.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String && t.GetString() is { } s)
                    tags.Add(s);
        }

        var codes = new List<int>();
        if (op.TryGetProperty("responses", out var resps) && resps.ValueKind == JsonValueKind.Object)
        {
            foreach (var r in resps.EnumerateObject())
                if (int.TryParse(r.Name, out var code) && code > 0)
                    codes.Add(code);
        }

        var schemes = new List<string>();
        if (op.TryGetProperty("security", out var sec) && sec.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in sec.EnumerateArray())
                if (req.ValueKind == JsonValueKind.Object)
                    foreach (var s in req.EnumerateObject())
                        if (!schemes.Contains(s.Name))
                            schemes.Add(s.Name);
        }

        return new LoadedOpenApiOperation(
            Path: path,
            Method: method,
            OperationId: operationId,
            Summary: summary,
            Description: description,
            Deprecated: deprecated,
            Tags: tags,
            ResponseStatusCodes: codes,
            SecuritySchemes: schemes);
    }

    private static string? TryGetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
