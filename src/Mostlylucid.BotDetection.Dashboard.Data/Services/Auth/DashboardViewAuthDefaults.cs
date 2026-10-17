namespace Mostlylucid.BotDetection.UI.Services.Auth;

/// <summary>
///     Well-known names for the FOSS dashboard view-auth seam. FOSS registers the
///     cookie scheme (<see cref="Scheme"/>) satisfying the authorization policy
///     (<see cref="PolicyName"/>). Commercial OIDC layers on by adding its own scheme
///     to the SAME policy — the dashboard middleware evaluates the policy (via
///     <c>IPolicyEvaluator</c>), so whatever schemes the policy names are honoured
///     together. This is the extension point that lets OIDC layer on WITHOUT forking
///     FOSS, and removes the old "OIDC is mutually exclusive with RequireAuthentication"
///     limitation.
/// </summary>
public static class DashboardViewAuthDefaults
{
    /// <summary>The FOSS cookie authentication scheme for dashboard view-auth.</summary>
    public const string Scheme = "StyloBotDashboardCookie";

    /// <summary>The authorization policy the dashboard middleware enforces for HTML viewing.</summary>
    public const string PolicyName = "stylobot-dashboard-view";
}
