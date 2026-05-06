using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

public sealed class ReactionPackEngine : BackgroundService
{
    private readonly IReadOnlyList<ReactionPackDefinition> _packs;
    private readonly DegradationAtom _atom;
    private readonly ReactionPackContext _context;
    private readonly ReactionRuleEvaluator _evaluator;
    private readonly ILogger<ReactionPackEngine> _logger;
    private readonly double _evaluationIntervalSeconds;

    private readonly Dictionary<string, Dictionary<int, (HysteresisTracker Activate, HysteresisTracker Deactivate)>> _trackers = [];
    private readonly Dictionary<string, int> _currentLevel = [];

    public ReactionPackEngine(
        IEnumerable<ReactionPackDefinition> packs,
        DegradationAtom atom,
        ReactionPackContext context,
        ReactionRuleEvaluator evaluator,
        ILogger<ReactionPackEngine> logger,
        double evaluationIntervalSeconds = 5.0)
    {
        _packs = packs.Where(p => p.Enabled).ToList();
        _atom = atom;
        _context = context;
        _evaluator = evaluator;
        _logger = logger;
        _evaluationIntervalSeconds = evaluationIntervalSeconds;

        foreach (var pack in _packs)
        {
            _trackers[pack.Name] = pack.Steps.ToDictionary(
                s => s.Level,
                _ => (new HysteresisTracker(), new HysteresisTracker()));
            _currentLevel[pack.Name] = 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_evaluationIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);
            EvaluateNow();
        }
    }

    public void EvaluateNow()
    {
        var signals = _atom.GetAvailableSignalKeys()
            .ToDictionary(k => k, k => _atom.GetSignalValue(k));

        foreach (var pack in _packs)
            EvaluatePack(pack, signals);
    }

    private void EvaluatePack(ReactionPackDefinition pack, Dictionary<string, double> signals)
    {
        var current = _currentLevel[pack.Name];
        var scope = pack.IsGlobal ? "global" : (pack.ScopedEndpoint ?? "global");

        var nextLevel = current + 1;
        var nextStep = pack.Steps.FirstOrDefault(s => s.Level == nextLevel);
        if (nextStep?.Activate != null)
        {
            var nextTrackers = _trackers[pack.Name][nextLevel];
            if (_evaluator.Evaluate(nextStep.Activate, signals, nextTrackers.Activate, $"{pack.Name}:L{nextLevel}:activate"))
            {
                _logger.LogInformation("Reaction pack '{Pack}' escalating {From} -> {To} (policy: {Policy})",
                    pack.Name, current, nextLevel, nextStep.Policy);
                _currentLevel[pack.Name] = nextLevel;
                _context.SetActiveLevel(pack.Name, nextLevel, nextStep.Policy, scope, pack.Priority);
                return;
            }
        }

        if (current <= 0)
            return;

        var currentStep = pack.Steps.FirstOrDefault(s => s.Level == current);
        if (currentStep?.Deactivate == null)
            return;

        var currentTrackers = _trackers[pack.Name][current];
        if (!_evaluator.Evaluate(currentStep.Deactivate, signals, currentTrackers.Deactivate, $"{pack.Name}:L{current}:deactivate"))
            return;

        var newLevel = 0;
        for (var l = current - 1; l >= 1; l--)
        {
            var lowerStep = pack.Steps.FirstOrDefault(s => s.Level == l);
            if (lowerStep == null) continue;
            if (lowerStep.Deactivate == null) { newLevel = l; break; }
            var lowerTrackers = _trackers[pack.Name][l];
            if (!_evaluator.Evaluate(lowerStep.Deactivate, signals, lowerTrackers.Deactivate, $"{pack.Name}:L{l}:deactivate"))
            {
                newLevel = l;
                break;
            }
        }

        _logger.LogInformation("Reaction pack '{Pack}' de-escalating {From} -> {To}", pack.Name, current, newLevel);
        _currentLevel[pack.Name] = newLevel;

        if (newLevel == 0)
            _context.Deactivate(pack.Name);
        else
        {
            var newStep = pack.Steps.First(s => s.Level == newLevel);
            _context.SetActiveLevel(pack.Name, newLevel, newStep.Policy, scope, pack.Priority);
        }
    }
}
