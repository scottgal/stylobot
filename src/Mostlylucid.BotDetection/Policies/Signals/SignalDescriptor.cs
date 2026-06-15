namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     A single entry in the signal catalogue. Combines what reflection knows
///     about a <c>SignalKeys</c> constant (the key string, its declaring type)
///     with what the XML doc comment supplies (<see cref="Short"/> /
///     <see cref="Long"/>) and what an optional VYaml overlay adds
///     (operator constraints, value domain, worked examples, related signals).
/// </summary>
/// <param name="Key">The signal key string -- the value of the <c>SignalKeys</c> constant (e.g. <c>risk.justification</c>).</param>
/// <param name="Kind">Value kind. Inferred from the XML doc summary's leading token (<c>String:</c>, <c>Bool:</c>, <c>Number:</c>, etc.) and overridable via the overlay.</param>
/// <param name="Short">First sentence of the XML doc <c>&lt;summary&gt;</c>. Used as the autocomplete tooltip headline.</param>
/// <param name="Long">Full XML doc <c>&lt;summary&gt;</c> body. Used as the detail pane.</param>
/// <param name="DeclaringType">Fully-qualified type name of the class that declares the <c>const string</c> (e.g. <c>Mostlylucid.BotDetection.Models.SignalKeys</c>).</param>
/// <param name="Operators">Predicate operators valid for this signal (e.g. <c>=</c>, <c>!=</c>, <c>in</c>). Defaulted from <see cref="Kind"/> when the overlay does not specify.</param>
/// <param name="Examples">Worked predicate snippets the editor can insert as starting points.</param>
/// <param name="Related">Other signal keys the editor should surface alongside this one (cross-reference hints).</param>
/// <param name="EmittedBy">
///     Detector/contributor name(s) that produce this signal, sourced from the
///     embedded <c>*.detector.yaml</c> manifests by inverting their
///     <c>emits.on_complete</c> + <c>emits.conditional</c> lists. The dashboard
///     "Source" column reads this to show provenance ("HeaderContributor" rather
///     than "SignalKeys"); empty for signals not declared by any detector
///     manifest (commercial/contributor packs that haven't shipped a manifest
///     yet, or signals written from one-off code paths).
/// </param>
public sealed record SignalDescriptor(
    string Key,
    SignalKind Kind,
    string Short,
    string Long,
    string DeclaringType,
    IReadOnlyList<string> Operators,
    IReadOnlyList<SignalExample> Examples,
    IReadOnlyList<string> Related,
    IReadOnlyList<string> EmittedBy);
