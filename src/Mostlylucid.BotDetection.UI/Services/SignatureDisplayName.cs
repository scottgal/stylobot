namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     SINGLE source of truth for rendering "the name of a signature row" in
///     every dashboard surface (Top Bots, Visitors, Sessions, Threats, Endpoint
///     Detail, Investigate, Signature Detail, Visitor Card).
///
///     This helper does NOTHING beyond pick between an operator override and
///     the upstream-set name, falling back to a single explicit placeholder
///     when both are empty. There is NO UA parsing here, NO catalog lookup,
///     NO uap-core call. The canonical name is set ONCE upstream by the
///     gateway's <c>FingerprintNameComposer</c> + <c>UserAgentContributor</c>
///     pipeline, persisted to <c>Fingerprint.DisplayName</c>, broadcast as
///     <c>DashboardDetectionEvent.BotName</c>, and projected into the cached
///     aggregate's <c>BotName</c>. Doing display-time resolution alongside
///     that pipeline rebuilds the matcher at the wrong layer and creates a
///     second name path -- exactly the parasitic pattern this code base has
///     fought repeatedly. If <see cref="Display"/> returns the placeholder,
///     the upstream pipeline is broken: fix it there, do not paper over here.
/// </summary>
public static class SignatureDisplayName
{
    /// <summary>
    ///     Placeholder rendered when neither the operator override nor the
    ///     upstream <c>BotName</c> is set. The em-dash matches the
    ///     "data cell with no value" convention used elsewhere in the
    ///     dashboard (UA / VER / etc. table cells). Visible-by-design so the
    ///     operator can tell at a glance that the matcher hasn't named the
    ///     row yet -- not a synth label, not a hash prefix.
    /// </summary>
    public const string Unnamed = "—";

    /// <summary>
    ///     Pick the visible name. Operator override wins; otherwise the
    ///     upstream-set <paramref name="botName"/>; otherwise the placeholder.
    /// </summary>
    public static string Display(string? botName, string? customLabel = null)
    {
        if (!string.IsNullOrWhiteSpace(customLabel)) return customLabel.Trim();
        if (!string.IsNullOrWhiteSpace(botName))     return botName.Trim();
        return Unnamed;
    }

    /// <summary>
    ///     Per-row title attribute exposing the full signature hash for
    ///     incident notes / operator grep. Never appears as the visible name.
    /// </summary>
    public static string TitleAttr(string signature) => $"Signature: {signature}";
}
