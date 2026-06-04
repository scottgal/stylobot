namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     Compiled, runtime-immutable view of one row from
///     <c>Definitions/BrowserModes/*.yaml</c>. A browser mode is the
///     request-shape class a browser is currently operating in (navigation,
///     xhr, sub-resource, signalr-negotiate, etc.) — same browser, different
///     modes per request.
///
///     See docs/architecture/composite-character-fingerprints.md.
/// </summary>
public sealed class BrowserMode
{
    public required string ModeId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required int Order { get; init; }
    public required BrowserModePredicate Predicate { get; init; }
}
