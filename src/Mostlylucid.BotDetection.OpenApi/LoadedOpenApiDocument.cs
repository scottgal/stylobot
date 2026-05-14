namespace Mostlylucid.BotDetection.OpenApi;

/// <summary>
///     Lightweight projection of a parsed OpenAPI document. Captures only the fields
///     downstream consumers (route catalog, auto-honeypot, SDK gen, policy seeding) need.
///     The full JSON is retained in <see cref="RawJson"/> for advanced inspection.
/// </summary>
public sealed record LoadedOpenApiDocument(
    string Source,
    string? Title,
    string? Version,
    IReadOnlyList<LoadedOpenApiOperation> Operations,
    DateTime LoadedUtc,
    string RawJson);

/// <summary>
///     Single documented operation. <see cref="Path"/> is the OpenAPI path template
///     ("/users/{id}"), <see cref="Method"/> is the lowercased HTTP verb. Together they
///     match a StyloBot route key after the verb is uppercased: "GET:/users/{id}".
/// </summary>
public sealed record LoadedOpenApiOperation(
    string Path,
    string Method,
    string? OperationId,
    string? Summary,
    string? Description,
    bool Deprecated,
    IReadOnlyList<string> Tags,
    IReadOnlyList<int> ResponseStatusCodes,
    IReadOnlyList<string> SecuritySchemes);
