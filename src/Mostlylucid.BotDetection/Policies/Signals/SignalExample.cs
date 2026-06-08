namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     A worked predicate example for a signal. <see cref="Expr"/> is the predicate
///     literal as it should appear in the expression editor; <see cref="Reads"/> is
///     a one-line plain-English read so the autocomplete tooltip can explain what
///     the predicate means without parsing the AST.
/// </summary>
public sealed record SignalExample(string Expr, string Reads);
