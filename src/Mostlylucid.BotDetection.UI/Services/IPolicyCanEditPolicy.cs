using System.Security.Claims;
using Microsoft.Extensions.Configuration;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Decides whether the policy-stack view component should render edit
///     affordances (pencil, drag handle, <c>+ Add rule</c>, Promote /
///     Cancel auto-promote, kind selector). The FOSS default always
///     returns <c>false</c> so the dashboard ships read-only by
///     construction; the commercial overlay registers a license-aware
///     implementation that flips the gate on for paid licenses + users
///     in the <c>dashboard-write</c> role.
///
///     <para>
///     The check is UI-level only- the security boundary is the
///     <c>[Authorize(Policy = AuthPolicies.DashboardWrite)]</c> attribute
///     on the commercial mutation API. This seam controls visibility of
///     edit affordances, not authorization of writes.
///     </para>
/// </summary>
public interface IPolicyCanEditPolicy
{
    /// <summary>
    ///     Returns <c>true</c> when the supplied principal is allowed to
    ///     see policy-stack edit affordances. Implementations MUST treat
    ///     a <c>null</c> or unauthenticated principal as read-only.
    /// </summary>
    bool CanEdit(ClaimsPrincipal? user);

    /// <summary>
    /// Controls how edit affordances are presented. FOSS and unentitled
    /// viewers remain hidden; commercial demo/showcase hosts can expose the
    /// real controls in a disabled, explanatory state without granting any
    /// write capability.
    /// </summary>
    PolicyEditAffordance GetEditAffordance(ClaimsPrincipal? user) =>
        CanEdit(user) ? PolicyEditAffordance.Editable : PolicyEditAffordance.Hidden;
}

public enum PolicyEditAffordance
{
    Hidden,
    ReadOnly,
    Editable
}

/// <summary>
///     Resolves the presentation state shared by the policy dashboard components. A showcase
///     dashboard deliberately exposes disabled controls even when the host has not installed the
///     commercial policy implementation; it never turns that configuration switch into write
///     permission.
/// </summary>
public static class PolicyEditAffordanceResolver
{
    public const string ShowcaseDemoConfigKey = "Dashboard:ShowcaseDemo";

    public static PolicyEditAffordance Resolve(
        IPolicyCanEditPolicy policy,
        ClaimsPrincipal? user,
        IConfiguration configuration)
    {
        var affordance = policy.GetEditAffordance(user);
        return affordance == PolicyEditAffordance.Hidden &&
               bool.TryParse(configuration[ShowcaseDemoConfigKey], out var showcaseDemo) && showcaseDemo
            ? PolicyEditAffordance.ReadOnly
            : affordance;
    }
}

/// <summary>
///     FOSS default. Always returns <c>false</c> so the dashboard ships
///     read-only and no operator can accidentally light up the edit
///     surface without an overlay. Commercial replaces this binding via
///     <c>services.Replace(...)</c> in its policy-edit service collection
///     extension.
/// </summary>
public sealed class AlwaysReadOnlyPolicyCanEditPolicy : IPolicyCanEditPolicy
{
    public bool CanEdit(ClaimsPrincipal? user) => false;
}
