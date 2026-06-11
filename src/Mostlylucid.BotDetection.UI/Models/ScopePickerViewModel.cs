using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for the <c>SbScopePicker</c> view component (T4 Part A).
///     Drives the four-axis form (Host / Method / Geo / Identity) that
///     produces a JSON projection of a <see cref="PolicyScope"/>. Each axis
///     is independently nullable; <c>none</c> on every axis is the wildcard
///     scope.
/// </summary>
/// <param name="FieldName">
///     The hidden-input name carrying the JSON projection in the wrapping
///     form. Defaults to <c>"scope"</c> so the typical
///     <c>&lt;form&gt;</c> submit binds it under that key. Pickers that need
///     a distinct field (e.g. multiple pickers in one form) override this.
/// </param>
/// <param name="Initial">
///     Pre-populate the picker with this scope on edits. <c>null</c> renders
///     the wildcard form (all axes <c>none</c>). Specified scopes restore
///     the matching axis controls; orthogonal slots not present on
///     <paramref name="Initial"/> render as <c>none</c>.
/// </param>
/// <param name="KnownMethods">
///     Method-axis dropdown options. Defaults to the standard HTTP verbs.
///     Operators can supply a narrower list (e.g. just <c>GET</c>/<c>POST</c>
///     for a customer that only enforces on those verbs).
/// </param>
/// <param name="KnownGeos">
///     Geo-axis dropdown options. Each entry is ISO-3166 alpha-2 code -&gt;
///     display name. Defaults to <c>null</c> which renders a free-text input
///     with no datalist suggestions; supply a curated list for the operator
///     to pick from.
/// </param>
/// <param name="KnownIdentityFamilies">
///     Identity-axis dropdown options. Used for the <c>NamedBot</c> family
///     and <c>HumanBrowser</c> family values. Defaults to <c>null</c> which
///     renders a free-text input.
/// </param>
public sealed record ScopePickerViewModel(
    string FieldName = "scope",
    PolicyScope? Initial = null,
    IReadOnlyList<string>? KnownMethods = null,
    IReadOnlyList<KeyValuePair<string, string>>? KnownGeos = null,
    IReadOnlyList<string>? KnownIdentityFamilies = null)
{
    /// <summary>
    ///     The default HTTP verbs offered when the caller does not supply a
    ///     curated method list. Mirrors the verbs Stylobot's detection
    ///     pipeline reports on <c>request.method</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultMethods =
        new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    /// <summary>The methods the picker should offer; honours <see cref="KnownMethods"/> when supplied.</summary>
    public IReadOnlyList<string> EffectiveMethods => KnownMethods ?? DefaultMethods;
}
