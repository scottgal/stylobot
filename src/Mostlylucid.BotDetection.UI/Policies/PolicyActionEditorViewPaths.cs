using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Policies;

/// <summary>
///     Single source of truth for the <c>kind -&gt; Razor view path</c> map
///     AND the <c>kind -&gt; edit-slice model</c> construction that back
///     <c>GET /dashboard/policystack/action-editor?kind=&lt;x&gt;</c>.
///
///     <para>
///     Both the production handler (<c>StyloBotDashboardMiddleware
///     .ServePolicyStackActionEditorAsync</c>) and the
///     <c>PolicyActionEditorTestController</c> (which mirrors the dispatch
///     so the partials can be rendered without booting the middleware's
///     full DI graph) call <see cref="ForKind"/> and
///     <see cref="BuildActionEditorModel"/>. That keeps the two paths in
///     lockstep when traffic-shaping plan Tasks 4-7 add the parameterised
///     kinds (tag / challenge / ratelimit / throttle) -- a new kind goes
///     here once, not twice. <c>feedback_no_duplication</c> is the
///     standing rule.
///     </para>
///
///     <para>
///     <c>internal</c> + <c>InternalsVisibleTo("Mostlylucid.BotDetection.Test")</c>
///     keeps the surface out of the public UI API while still allowing the
///     test assembly to share it.
///     </para>
/// </summary>
internal static class PolicyActionEditorViewPaths
{
    private const string ViewRoot = "/Views/Shared/Components/SbPolicyStack/";

    /// <summary>
    ///     Maps a lower-case action kind to the absolute Razor view path
    ///     <c>GET /dashboard/policystack/action-editor</c> renders. Returns
    ///     <c>null</c> for unknown kinds; the caller is responsible for
    ///     turning that into a 404.
    /// </summary>
    /// <param name="kind">
    ///     Lower-case action kind (<c>allow</c>, <c>observe</c>, <c>block</c>,
    ///     <c>tag</c>, <c>challenge</c> today; <c>ratelimit</c> /
    ///     <c>throttle</c> arrive in Tasks 6-7).
    /// </param>
    public static string? ForKind(string kind) => kind switch
    {
        "allow"     => ViewRoot + "_EditAction_Allow.cshtml",
        "observe"   => ViewRoot + "_EditAction_Observe.cshtml",
        "block"     => ViewRoot + "_EditAction_Block.cshtml",
        "tag"       => ViewRoot + "_EditAction_Tag.cshtml",
        "challenge" => ViewRoot + "_EditAction_Challenge.cshtml",
        // Tasks 6-7 add ratelimit / throttle here.
        _ => null
    };

    /// <summary>
    ///     Builds the per-kind <c>@model</c> instance from the request
    ///     query string. Zero-field partials (Allow / Observe / Block)
    ///     return <c>null</c> -- they have no <c>@model</c> directive so
    ///     any non-null object would round-trip fine but null avoids the
    ///     unnecessary allocation. The caller is responsible for swapping
    ///     <c>null</c> for a sentinel (<c>new object()</c>) before handing
    ///     it to <c>RazorViewRenderer.RenderViewToStringAsync</c>, which
    ///     declares <c>model</c> non-nullable.
    /// </summary>
    /// <param name="kind">
    ///     Lower-case action kind as returned by the query parser.
    /// </param>
    /// <param name="query">
    ///     Request query collection -- per-field defaults are read from
    ///     here so the editor JS can seed the partial with the row's
    ///     current values when it issues the swap.
    /// </param>
    public static object? BuildActionEditorModel(string kind, IQueryCollection query) => kind switch
    {
        "tag" => new TagActionEdit(query["name"].ToString() ?? string.Empty),
        "challenge" => new ChallengeActionEdit(
            // Defaulting to "turnstile" keeps the partial's <select> first
            // option aligned with the model when the editor hasn't seeded
            // a value (e.g. operator just flipped action-kind to
            // "challenge" with no prior state). The site-key sub-field
            // only renders for turnstile/captcha; the partial owns that
            // branch so the model carries the raw value either way.
            Kind: query["challengeKind"].ToString() is var k && !string.IsNullOrEmpty(k) ? k : "turnstile",
            SiteKey: query["siteKey"].ToString()),
        // Zero-field kinds (allow / observe / block) have no @model
        // directive on their partial. Tasks 6-7 add ratelimit / throttle
        // cases here.
        _ => null
    };
}