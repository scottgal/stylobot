namespace Mostlylucid.BotDetection.UI.Configuration;

/// <summary>
///     FOSS dashboard view-authentication mode. Static, config/env-only; no IdP.
///     Commercial OIDC layers on top of this via the shared authorization policy
///     (see <c>DashboardViewAuthDefaults.PolicyName</c>) without forking FOSS.
/// </summary>
public enum DashboardAuthMode
{
    /// <summary>
    ///     No view-auth from this feature. Back-compat: the dashboard's existing
    ///     authorization behaviour (<c>RequireAuthentication</c> / <c>AllowUnauthenticatedAccess</c>
    ///     / <c>AuthorizationFilter</c> / dev-vs-prod default) is untouched. A startup
    ///     warning nudges operators to configure a credential.
    /// </summary>
    None = 0,

    /// <summary>
    ///     Config-credential cookie login. A styled login page gates the human-facing
    ///     <c>{BasePath}/*</c> HTML routes; the login POST verifies a single
    ///     <see cref="DashboardAuthOptions.Username"/> + <see cref="DashboardAuthOptions.PasswordHash"/>
    ///     held in config/env. No user database. Generate the hash with
    ///     <c>stylobot dashboard hash-password</c>.
    /// </summary>
    Login = 1
}

/// <summary>
///     FOSS dashboard view-auth options, bound from <c>StyloBot:Dashboard:Auth</c>
///     (a nested section of <see cref="StyloBotDashboardOptions"/>, alongside the
///     existing auth knobs <c>RequireAuthentication</c>/<c>AllowUnauthenticatedAccess</c>).
///     Enforcement is opt-in: only active when <see cref="Mode"/> is
///     <see cref="DashboardAuthMode.Login"/> AND a credential is configured.
/// </summary>
public sealed class DashboardAuthOptions
{
    /// <summary>Which view-auth mode to enforce. Default <see cref="DashboardAuthMode.None"/> (inert, back-compat).</summary>
    public DashboardAuthMode Mode { get; set; } = DashboardAuthMode.None;

    /// <summary>The single dashboard login username. Bindable from env (double-underscore).</summary>
    public string? Username { get; set; }

    /// <summary>
    ///     PBKDF2 password hash string produced by <c>stylobot dashboard hash-password</c>.
    ///     NEVER a plaintext password. Self-describing/self-contained so verification is clean.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>Auth cookie name. Default <c>sb.dashboard.auth</c>.</summary>
    public string CookieName { get; set; } = "sb.dashboard.auth";

    /// <summary>Sliding cookie expiry in minutes. Default 480 (8h).</summary>
    public int SlidingExpirationMinutes { get; set; } = 480;

    /// <summary>True only when Login mode is selected AND a username + hash are both present.</summary>
    public bool IsConfigured =>
        Mode == DashboardAuthMode.Login
        && !string.IsNullOrEmpty(Username)
        && !string.IsNullOrEmpty(PasswordHash);
}
