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

    public SbPolicyStackViewComponent(PolicyStackPresenter presenter)
    {
        _presenter = presenter;
    }

    /// <summary>
    ///     Build and render the stack control. <paramref name="canEdit"/> is
    ///     plumbed through the view model but B1+B2 does NOT render edit
    ///     affordances -- that surface lands in B6+C.
    ///     <paramref name="filterExpression"/> / <paramref name="sortKey"/> /
    ///     <paramref name="sortDir"/> arrive from the URL query and are parsed
    ///     by the model types -- unknown tokens degrade gracefully to the
    ///     default empty filter / default sort.
    /// </summary>
    public async Task<IViewComponentResult> InvokeAsync(
        PolicyScope scope,
        PolicyStackEmbed embed = PolicyStackEmbed.Full,
        string? activeTab = null,
        TimeSpan? aggregateWindow = null,
        bool canEdit = false,
        string? filterExpression = null,
        string? sortKey = null,
        string? sortDir = null,
        string? explainerFingerprint = null,
        bool lockedFingerprint = false)
    {
        var filter = PolicyStackFilter.Parse(filterExpression);
        var sort = PolicyStackSort.Parse(sortKey, sortDir);

        var vm = await _presenter.BuildAsync(
            scope: scope,
            embed: embed,
            activeTab: activeTab ?? "effective",
            aggregateWindow: aggregateWindow ?? TimeSpan.FromHours(24),
            canEdit: canEdit,
            filter: filter,
            sort: sort,
            explainerFingerprint: explainerFingerprint,
            explainerLocked: lockedFingerprint,
            ct: HttpContext?.RequestAborted ?? CancellationToken.None);

        // Default.cshtml is the single entry point -- it dispatches to the
        // embed-shape partial based on vm.Embed. Returning View() (i.e.
        // Default) keeps the view-component locator happy without us having
        // to publish three sibling top-level views.
        return View(vm);
    }
}
