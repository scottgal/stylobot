namespace Mostlylucid.BotDetection.UI.Policies;

/// <summary>
///     Single source of truth for the <c>kind -&gt; Razor view path</c> map
///     that backs <c>GET /dashboard/policystack/action-editor?kind=&lt;x&gt;</c>.
///
///     <para>
///     Both the production handler (<c>StyloBotDashboardMiddleware
///     .ServePolicyStackActionEditorAsync</c>) and the
///     <c>PolicyActionEditorTestController</c> (which mirrors the dispatch
///     so the partials can be rendered without booting the middleware's
///     full DI graph) call <see cref="ForKind"/>. That keeps the two paths
///     in lockstep when traffic-shaping plan Tasks 4-7 add the
///     parameterised kinds (tag / challenge / ratelimit / throttle) -- a
///     new kind goes here once, not twice.
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
    ///     Lower-case action kind (<c>allow</c>, <c>observe</c>, <c>block</c>
    ///     today; <c>tag</c> / <c>challenge</c> / <c>ratelimit</c> /
    ///     <c>throttle</c> arrive in Tasks 4-7).
    /// </param>
    public static string? ForKind(string kind) => kind switch
    {
        "allow"   => ViewRoot + "_EditAction_Allow.cshtml",
        "observe" => ViewRoot + "_EditAction_Observe.cshtml",
        "block"   => ViewRoot + "_EditAction_Block.cshtml",
        // Tasks 4-7 add tag / challenge / ratelimit / throttle here.
        _ => null
    };
}