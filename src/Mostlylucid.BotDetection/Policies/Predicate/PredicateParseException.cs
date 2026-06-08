namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Thrown by <see cref="PredicateParser.Parse"/> when the input cannot
///     be parsed. Carries the failing character position so the editor can
///     anchor the inline error indicator.
/// </summary>
public sealed class PredicateParseException : Exception
{
    public int Position { get; }
    public PredicateParseException(string message, int position)
        : base($"parse error at char {position}: {message}")
    {
        Position = position;
    }
}
