namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     Thrown by <see cref="ISignalCatalog.Require"/> when a caller asks for a
///     signal key the catalogue does not know. The message embeds the
///     Levenshtein-nearest known keys so editor + CLI error surfaces can
///     answer "did you mean &lt;x&gt;?" without re-running the search.
/// </summary>
public sealed class UnknownSignalException : Exception
{
    /// <summary>
    ///     The signal key the caller asked for. Preserved verbatim so callers
    ///     can echo it back in their own error surfaces.
    /// </summary>
    public string Key { get; }

    /// <summary>
    ///     The top suggestions the catalogue offered (Levenshtein-sorted).
    ///     Empty when the catalogue is empty.
    /// </summary>
    public IReadOnlyList<string> Suggestions { get; }

    public UnknownSignalException(string key, IReadOnlyList<string> suggestions)
        : base(BuildMessage(key, suggestions))
    {
        Key = key;
        Suggestions = suggestions;
    }

    private static string BuildMessage(string key, IReadOnlyList<string> suggestions)
    {
        if (suggestions.Count == 0)
            return $"Unknown signal key '{key}'. The catalogue has no suggestions.";
        return $"Unknown signal key '{key}'. Did you mean: {string.Join(", ", suggestions)}?";
    }
}
