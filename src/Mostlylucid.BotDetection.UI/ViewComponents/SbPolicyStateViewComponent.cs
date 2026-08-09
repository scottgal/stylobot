using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     <c>sb-policy-state</c>: per-policy live state on the /dashboard/policies surface. Renders
///     every registered action policy with the effective params it contributed via
///     <see cref="IPolicyStateContributor"/> (content-cache policies expose representation, match,
///     cache mode, configured bounds and hit/miss/bypass/eviction/override counters), plus any
///     policy configured under <c>StyloExtract:Actions</c> whose implementation is NOT registered —
///     per the content-cache spec a configured policy is not considered enabled unless its action
///     implementation is registered and its row resolves to it.
///     <para>
///         Optional-DI: the invocation in _Policies.cshtml is gated on <see cref="IPolicyStateProvider"/>
///         being registered (which only happens where <see cref="IActionPolicyRegistry"/> exists —
///         never on a thin-client / remote-mode host, where a locally-fabricated registry would render
///         a wrong baseline; the same guard EffectivePolicyComposer uses).
///     </para>
/// </summary>
public sealed class SbPolicyStateViewComponent : ViewComponent
{
    private readonly IPolicyStateProvider _provider;
    private readonly IActionPolicyRegistry _registry;
    private readonly IConfiguration _configuration;

    public SbPolicyStateViewComponent(
        IPolicyStateProvider provider,
        IActionPolicyRegistry registry,
        IConfiguration configuration)
    {
        _provider = provider;
        _registry = registry;
        _configuration = configuration;
    }

    public IViewComponentResult Invoke()
    {
        var rows = new List<SbPolicyStateRowViewModel>();

        foreach (var state in _provider.GetAll())
        {
            rows.Add(new SbPolicyStateRowViewModel(
                state.Name,
                state.Intent.ToString(),
                IsEnabled: true,
                EnabledReason: null,
                Params: state.EffectiveParams));
        }

        // Configured-but-unregistered: names present under StyloExtract:Actions whose action
        // implementation is not in the registry (e.g. the StyloExtract pack was never added).
        // They must render as NOT enabled — "configured" alone is never "enabled".
        var configuredNames = _configuration
            .GetSection("StyloExtract:Actions")
            .GetChildren()
            .Select(child => child.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key));

        foreach (var name in configuredNames)
        {
            if (_registry.GetPolicy(name) is not null) continue;
            rows.Add(new SbPolicyStateRowViewModel(
                name,
                "—",
                IsEnabled: false,
                EnabledReason: "configured but no implementation registered (add the StyloExtract pack)",
                Params: new Dictionary<string, object>()));
        }

        return View(rows);
    }
}
