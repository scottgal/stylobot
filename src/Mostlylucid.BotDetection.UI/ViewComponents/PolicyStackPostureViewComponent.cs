using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Atoms;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

public sealed class PolicyStackPostureViewComponent : ViewComponent
{
    private readonly PolicyStackPostureClassifier _classifier;
    private readonly PolicyStackHitAtom _atom;

    public PolicyStackPostureViewComponent(
        PolicyStackPostureClassifier classifier,
        PolicyStackHitAtom atom)
    {
        _classifier = classifier;
        _atom = atom;
    }

    public Task<IViewComponentResult> InvokeAsync(IReadOnlyList<PolicyRule> rules, string scopeKey)
    {
        var snapshot = _atom.Snapshot(scopeKey, TimeSpan.FromHours(24));
        var posture = _classifier.Classify(rules, snapshot);
        return Task.FromResult<IViewComponentResult>(View("Default", posture));
    }
}