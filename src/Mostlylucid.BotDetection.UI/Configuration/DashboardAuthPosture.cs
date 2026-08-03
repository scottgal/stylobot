namespace Mostlylucid.BotDetection.UI.Configuration;

/// <summary>
///     Produces a one-time startup advisory about the dashboard's view-auth posture,
///     so a self-hoster is nudged to lock the dashboard down (or is told their Login
///     config is incomplete). Pure/side-effect-free so it can be unit-tested; the
///     caller (<c>UseStyloBot</c>) logs the result.
/// </summary>
public static class DashboardAuthPosture
{
    /// <summary>
    ///     Returns an advisory message, or <c>null</c> when auth is configured (or open
    ///     access is explicit) and nothing needs saying.
    /// </summary>
    public static string? Advisory(StyloBotDashboardOptions options)
    {
        var auth = options.Auth;

        // Login selected but the credential is incomplete → the feature is inert; say so loudly.
        if (auth.Mode == DashboardAuthMode.Login && !auth.IsConfigured)
            return "StyloBot:Dashboard:Auth:Mode=Login but Username/PasswordHash are not both set — "
                   + "dashboard login is NOT enforced. Generate a hash with 'stylobot dashboard hash-password'.";

        // Nothing gating the dashboard at all → nudge toward configuring a login.
        if (auth.Mode == DashboardAuthMode.None
            && !options.RequireAuthentication
            && !options.AllowUnauthenticatedAccess
            && options.AuthorizationFilter is null
            && string.IsNullOrEmpty(options.RequireAuthorizationPolicy))
            return "Dashboard has no view-auth configured. Set StyloBot:Dashboard:Auth:Mode=Login "
                   + "(with Username + PasswordHash) to require a login, or set AllowUnauthenticatedAccess=true "
                   + "to explicitly allow open access.";

        return null;
    }
}
