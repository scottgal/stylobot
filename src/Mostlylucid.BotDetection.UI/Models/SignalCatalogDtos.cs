namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     DTOs the dashboard <c>/dashboard/signals/*</c> autocomplete API serialises.
///     Kept separate from <see cref="Mostlylucid.BotDetection.Policies.Signals.SignalDescriptor"/>
///     so the HTTP wire shape can evolve without changing the catalogue contract --
///     the catalogue is an internal vocabulary, these records are the public surface
///     the expression editor binds to.
/// </summary>
public sealed record SignalNamespaceDto(string Name, int Count);

public sealed record SignalNamespacesResponseDto(IReadOnlyList<SignalNamespaceDto> Items);

public sealed record SignalSearchHitDto(
    string Key,
    string Kind,
    string Short,
    IReadOnlyList<string> Operators);

public sealed record SignalSearchResponseDto(IReadOnlyList<SignalSearchHitDto> Items);

public sealed record SignalExampleDto(string Expr, string Reads);

public sealed record SignalFullDto(
    string Key,
    string Kind,
    string Short,
    string Long,
    IReadOnlyList<string> Operators,
    IReadOnlyList<SignalExampleDto> Examples,
    IReadOnlyList<string> Related,
    string DeclaringType);

/// <summary>Error envelope returned when a key is unknown; carries Levenshtein suggestions.</summary>
public sealed record SignalUnknownErrorDto(string Error, IReadOnlyList<string> Suggestions);
