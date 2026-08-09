using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Configuration;

/// <summary>
///     THE single place dashboard access is resolved from configuration.
///
///     <para>
///         Why this exists. The dashboard PAGE is served by one process (the website) and
///         the SignalR HUB is mapped in another (the gateway). Each built its own
///         <see cref="StyloBotDashboardOptions"/>: the website read
///         <c>STYLOBOT_DASHBOARD_PUBLIC</c> / <c>STYLOBOT_DASHBOARD_SECRET</c> and configured
///         auth properly, while <c>AddBotDetectionPersistence</c> registered a bare
///         <c>new StyloBotDashboardOptions { Enabled = true }</c> with nothing else set. On the
///         gateway every auth branch therefore fell through to deny — deterministically,
///         whatever the page resolved — and the hub aborted every connection with "dashboard
///         auth failed" (2026-08-09). Live updates were dead: the dashboard rendered once and
///         froze, because Signal Shingle's update path is SignalR beacon → HTMX OOB swap.
///     </para>
///
///     <para>
///         An earlier fix made the hub's LOGIC identical to the middleware's. That was
///         necessary and not sufficient: <b>identical logic over different configuration still
///         produces different verdicts.</b> "The hub enforces the same rules as the middleware"
///         needs the same RULES and the same INPUTS; only the rules had been addressed.
///     </para>
///
///     <para>
///         So this is deliberately ONE function rather than a block copied into each host.
///         Two hosts reading the same environment variable independently is the same
///         two-sources-of-truth trap in a new place — it diverges the next time an auth mode
///         is added to one and not the other, which is exactly how the original bug happened.
///         Add a mode here and both hosts get it.
///     </para>
/// </summary>
public static class DashboardAccessResolver
{
    /// <summary>Header carrying the dashboard access secret. Never a query string.</summary>
    public const string SecretHeader = "X-SB-Dashboard-Secret";

    /// <summary>Config key / environment variable pairs, so both hosts read the same inputs.</summary>
    public const string PublicConfigKey = "StyloBotDashboard:Public";
    public const string PublicEnvVar = "STYLOBOT_DASHBOARD_PUBLIC";
    public const string SecretConfigKey = "StyloBotDashboard:AccessSecret";
    public const string SecretEnvVar = "STYLOBOT_DASHBOARD_SECRET";

    /// <summary>
    ///     Resolves <see cref="StyloBotDashboardOptions.AllowUnauthenticatedAccess"/> and
    ///     <see cref="StyloBotDashboardOptions.AuthorizationFilter"/> from configuration and
    ///     environment, and applies them to <paramref name="options"/>.
    ///
    ///     <para>
    ///         Does NOT touch <c>Auth.Mode</c> / <c>RequireAuthentication</c> /
    ///         <c>RequireAuthorizationPolicy</c>: those are stronger, explicitly-configured
    ///         gates that run BEFORE the filter in both the middleware and the hub, and this
    ///         resolver must never be able to loosen one. It only fills in the access posture
    ///         a host would otherwise leave at its (deny-everything) default.
    ///     </para>
    /// </summary>
    public static StyloBotDashboardOptions ApplyAccessPosture(
        StyloBotDashboardOptions options,
        IConfiguration? configuration,
        bool isDevelopment)
    {
        var isPublic = ReadBool(configuration, PublicConfigKey, PublicEnvVar, isDevelopment);
        var secret = ReadString(configuration, SecretConfigKey, SecretEnvVar);

        options.AllowUnauthenticatedAccess = isPublic;

        options.AuthorizationFilter = context =>
        {
            if (isPublic) return Task.FromResult(true);
            if (!string.IsNullOrEmpty(secret) && CarriesSecret(context, secret))
                return Task.FromResult(true);
            return Task.FromResult(context.User?.Identity?.IsAuthenticated == true);
        };

        return options;
    }

    /// <summary>
    ///     True when the request carries the dashboard access secret in
    ///     <see cref="SecretHeader"/>. Deliberately NOT a query string: a capability token in
    ///     a URL leaks through access logs, browser history and referer headers.
    ///
    ///     <para>
    ///         GET/HEAD only — a secret in a form body is never accepted and non-idempotent
    ///         methods are never opened by it. NOTE for the hub: a SignalR handshake POSTs to
    ///         <c>/negotiate</c>, so this path deliberately does NOT admit a hub connection.
    ///         A browser never sends this header anyway (it is an operator/CI affordance), so
    ///         hub access comes from the public flag or a real authenticated principal — not
    ///         from relaxing this rule.
    ///     </para>
    /// </summary>
    public static bool CarriesSecret(HttpContext context, string expectedSecret)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            return false;

        var provided = context.Request.Headers[SecretHeader].FirstOrDefault();
        return !string.IsNullOrEmpty(provided) && FixedTimeEquals(expectedSecret, provided);
    }

    private static bool ReadBool(IConfiguration? cfg, string key, string envVar, bool fallback)
    {
        var raw = cfg?[key] ?? Environment.GetEnvironmentVariable(envVar);
        return bool.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    private static string ReadString(IConfiguration? cfg, string key, string envVar)
    {
        var raw = cfg?[key];
        return !string.IsNullOrWhiteSpace(raw)
            ? raw
            : Environment.GetEnvironmentVariable(envVar) ?? "";
    }

    /// <summary>Constant-time compare: a secret check must not leak length or prefix by timing.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
