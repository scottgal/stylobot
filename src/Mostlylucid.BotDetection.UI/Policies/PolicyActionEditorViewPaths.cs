using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

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
///     <see cref="BuildActionEditorModel"/>. The lockstep keeps the two
///     paths from drifting: every
///     <see cref="Mostlylucid.BotDetection.Policies.Rules.PolicyAction"/>
///     kind has a corresponding partial routed through here, so a new
///     kind goes here once, not twice. <c>feedback_no_duplication</c> is
///     the standing rule.
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
    ///     Lower-case action kind: one of <c>allow</c>, <c>observe</c>,
    ///     <c>block</c>, <c>tag</c>, <c>challenge</c>, <c>ratelimit</c>,
    ///     <c>throttle</c> -- the complete 7-kind matrix mirroring
    ///     <see cref="Mostlylucid.BotDetection.Policies.Rules.PolicyAction"/>.
    /// </param>
    public static string? ForKind(string kind) => kind switch
    {
        "allow"     => ViewRoot + "_EditAction_Allow.cshtml",
        "observe"   => ViewRoot + "_EditAction_Observe.cshtml",
        "block"     => ViewRoot + "_EditAction_Block.cshtml",
        "tag"       => ViewRoot + "_EditAction_Tag.cshtml",
        "challenge" => ViewRoot + "_EditAction_Challenge.cshtml",
        "ratelimit" => ViewRoot + "_EditAction_RateLimit.cshtml",
        "throttle"  => ViewRoot + "_EditAction_Throttle.cshtml",
        _ => null
    };

    /// <summary>
    ///     Display labels for every supported action kind, in the order they
    ///     should appear in the action-kind selector in
    ///     <c>_EditAction.cshtml</c>. Single source of truth shared with
    ///     <see cref="ForKind"/> -- adding a kind here without adding it to
    ///     <see cref="ForKind"/> (or vice versa) is an inconsistency the
    ///     unit test catches. The selector partial enumerates this list to
    ///     render <c>&lt;option&gt;</c> rows instead of hand-rolling a
    ///     parallel literal (the duplication smell
    ///     <c>feedback_no_word_lists</c> calls out).
    /// </summary>
    public static IReadOnlyList<(string Kind, string Label)> KindsForSelector { get; } = new[]
    {
        ("allow",     "Allow"),
        ("observe",   "Observe"),
        ("tag",       "Tag"),
        ("challenge", "Challenge"),
        ("ratelimit", "Rate limit"),
        ("throttle",  "Throttle (scope-wide)"),
        ("block",     "Block"),
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
        "ratelimit" => new RateLimitActionEdit(
            // Defaults mirror the spec table:
            //   rate = 6 / minute, key = fingerprint,
            //   over-limit = throttle-status, burst/mitigation = null.
            // The two string defaults (Key / OverLimitAction) reuse the
            // const literals on PolicyEditPresenter -- same string in
            // two places is the duplication smell `feedback_no_duplication`
            // calls out, and the presenter's legacy widening (when the
            // stored PolicyAction.RateLimit only carries RequestsPerMinute)
            // already canonicalised the values there.
            Rate: int.TryParse(query["rate"], out var r) ? r : 6,
            Unit: query["unit"].ToString() is var u && !string.IsNullOrEmpty(u) ? u : "minute",
            Key:  query["key"].ToString()  is var k2 && !string.IsNullOrEmpty(k2) ? k2 : PolicyEditPresenter.DefaultRateLimitKey,
            Burst: int.TryParse(query["burst"], out var b) ? b : null,
            MitigationTimeoutSeconds: int.TryParse(query["mitigation"], out var mt) ? mt : null,
            OverLimitAction: query["over"].ToString() is var o && !string.IsNullOrEmpty(o) ? o : PolicyEditPresenter.DefaultOverLimitAction),
        "throttle" => new ThrottleActionEdit(
            // Default RPS = 10 satisfies the > 0 bound the
            // PolicyAction.Throttle constructor enforces server-side --
            // the partial surfaces that same bound as min="1" on the
            // <input>. We don't re-declare it as a local const here
            // (`feedback_no_duplication`); the canonical bound lives on
            // the action record's constructor.
            RequestsPerSecond: int.TryParse(query["rps"], out var rps) ? rps : 10,
            Reason: query["reason"].ToString()),
        // Zero-field kinds (allow / observe / block) have no @model
        // directive on their partial -- the dispatch returns null and the
        // caller swaps in a sentinel before handing to the renderer.
        _ => null
    };
}