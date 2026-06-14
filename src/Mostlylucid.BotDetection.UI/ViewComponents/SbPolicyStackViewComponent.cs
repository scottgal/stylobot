using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     <c>sb-policy-stack</c> view component. Renders the Policy Stack control
///     for a given <see cref="PolicyScope"/> in one of three embed shapes:
///     <see cref="PolicyStackEmbed.Full"/> (breadcrumb + tabs + rows),
///     <see cref="PolicyStackEmbed.EffectiveOnly"/> (just the rule list), or
///     <see cref="PolicyStackEmbed.StatusBadge"/> (a single-line summary).
///     The presenter does all the work; the view component is a thin shell
///     that maps the embed enum to the right partial.
/// </summary>
public sealed class SbPolicyStackViewComponent : ViewComponent
{
    private readonly PolicyStackPresenter _presenter;
    private readonly IPolicyCanEditPolicy _canEditPolicy;

    public SbPolicyStackViewComponent(
        PolicyStackPresenter presenter,
        IPolicyCanEditPolicy canEditPolicy)
    {
        _presenter = presenter;
        _canEditPolicy = canEditPolicy;
    }

    /// <summary>
    ///     Build and render the stack control. <paramref name="canEdit"/> is
    ///     a nullable override- when set, it wins (lets tests and snapshot
    ///     views force read-only or read-write regardless of identity).
    ///     When unset (the production path), the registered
    ///     <see cref="IPolicyCanEditPolicy"/> decides against the current
    ///     <c>HttpContext.User</c>. The FOSS default returns <c>false</c>
    ///     always; the commercial overlay gates on license + role.
    ///     <paramref name="filterExpression"/> / <paramref name="sortKey"/> /
    ///     <paramref name="sortDir"/> arrive from the URL query and are parsed
    ///     by the model types- unknown tokens degrade gracefully to the
    ///     default empty filter / default sort.
    /// </summary>
    public async Task<IViewComponentResult> InvokeAsync(
        PolicyScope scope,
        PolicyStackEmbed embed = PolicyStackEmbed.Full,
        string? activeTab = null,
        TimeSpan? aggregateWindow = null,
        bool? canEdit = null,
        string? filterExpression = null,
        string? sortKey = null,
        string? sortDir = null,
        string? explainerFingerprint = null,
        bool lockedFingerprint = false,
        bool hideRuleList = false)
    {
        var filter = PolicyStackFilter.Parse(filterExpression);
        var sort = PolicyStackSort.Parse(sortKey, sortDir);

        // The optional canEdit parameter stays as a manual override for
        // tests + snapshot views; when unset the service decision wins.
        // Service is consulted lazily so callers that pin canEdit don't
        // pay for the principal lookup.
        var effectiveCanEdit = canEdit ?? _canEditPolicy.CanEdit(HttpContext?.User);

        var vm = await _presenter.BuildAsync(
            scope: scope,
            embed: embed,
            activeTab: activeTab ?? "effective",
            aggregateWindow: aggregateWindow ?? TimeSpan.FromHours(24),
            canEdit: effectiveCanEdit,
            filter: filter,
            sort: sort,
            explainerFingerprint: explainerFingerprint,
            explainerLocked: lockedFingerprint,
            hideRuleList: hideRuleList,
            ct: HttpContext?.RequestAborted ?? CancellationToken.None);

        // Default.cshtml is the single entry point -- it dispatches to the
        // embed-shape partial based on vm.Embed. Returning View() (i.e.
        // Default) keeps the view-component locator happy without us having
        // to publish three sibling top-level views.
        return View(vm);
    }
}