using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Minimal deserialization envelope mirroring the gateway's <c>PaginatedResponse&lt;T&gt;</c>
///     and <c>SingleResponse&lt;T&gt;</c> shapes - both expose <c>data</c> as the payload root.
///     The UI project cannot reference <c>Mostlylucid.BotDetection.Api</c> (the Api project
///     references UI for its models, so the reverse would cycle), so we deserialize through
///     this private envelope and discard pagination metadata - the remote viewer doesn't
///     drive paging from the envelope; it asks for what it wants per call.
/// </summary>
internal sealed record RemoteEnvelope<T>(
    [property: JsonPropertyName("data")] T? Data);
