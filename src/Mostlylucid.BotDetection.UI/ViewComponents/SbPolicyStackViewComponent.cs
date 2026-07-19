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
        var affordance = canEdit switch
        {
            true => PolicyEditAffordance.Editable,
            false => PolicyEditAffordance.Hidden,
            null => _canEditPolicy.GetEditAffordance(HttpContext?.User)
        };
        var effectiveCanEdit = affordance == PolicyEditAffordance.Editable;

        // Per-VC render budget: cap presenter.BuildAsync at 2s so a slow
        // upstream (remote-viewer HttpClient, DB query) can't block the
        // dashboard shell. Serial VCs summed under the render-loop ceiling;
        // timeout degrades to the empty presenter model. Matches the pattern
        // in stylobot-commercial 8989761 and stylobot SbSiteHealth d18f7fba.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext?.RequestAborted ?? CancellationToken.None);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        PolicyStackViewModel vm;
        try
        {
            vm = await _presenter.BuildAsync(
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
                ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            vm = new PolicyStackViewModel(
                Scope: scope,
                BreadcrumbPath: Array.Empty<PolicyScope>(),
                Embed: embed,
                ActiveTab: activeTab ?? "effective",
                AggregateWindow: aggregateWindow ?? TimeSpan.FromHours(24),
                CanEdit: effectiveCanEdit,
                Rows: Array.Empty<PolicyStackRowViewModel>(),
                TotalEffectiveRules: 0,
                RulesTriggeredInWindow: 0,
                LatestEditAt: null,
                ScopeHash: string.Empty,
                StackGroups: Array.Empty<PolicyStackScopeGroupViewModel>(),
                Conflicts: Array.Empty<PolicyConflictViewModel>(),
                Filter: filter,
                Sort: sort,
                AggregateStrip: null);
        }

        if (affordance == PolicyEditAffordance.ReadOnly)
            vm = WithReadOnlyAffordances(vm);

        vm = vm with { EditAffordance = affordance };

        // Default.cshtml is the single entry point -- it dispatches to the
        // embed-shape partial based on vm.Embed. Returning View() (i.e.
        // Default) keeps the view-component locator happy without us having
        // to publish three sibling top-level views.
        return View(vm);
    }

    private static PolicyStackViewModel WithReadOnlyAffordances(PolicyStackViewModel model)
    {
        var rows = model.Rows.Select(row => row with { ShowReadOnlyEditAffordance = !row.IsInherited }).ToArray();
        var groups = model.StackGroups.Select(group => group with
        {
            Rows = group.Rows.Select(row => row with { ShowReadOnlyEditAffordance = !row.IsInherited }).ToArray(),
            OwnedRules = group.OwnedRules.Select(row => row with { ShowReadOnlyEditAffordance = !row.IsInherited }).ToArray(),
            ShowReadOnlyAddAffordance = true
        }).ToArray();

        return model with { Rows = rows, StackGroups = groups };
    }
}
