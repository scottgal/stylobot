using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Reputation state for a pattern - determines how it's used in fast path.
/// </summary>
public enum ReputationState
{
    /// <summary>No strong signal either way - pattern barely contributes to scoring</summary>
    Neutral,

    /// <summary>Some evidence of bot behavior - contributes to score but can't trigger abort alone</summary>
    Suspect,

    /// <summary>Strong evidence of bot behavior - can trigger fast-path abort</summary>
    ConfirmedBad,

    /// <summary>Evidence of legitimate behavior - may lower risk score</summary>
    ConfirmedGood,

    /// <summary>Manually blocked by admin - never auto-downgrade</summary>
    ManuallyBlocked,

    /// <summary>Manually allowed by admin - never auto-upgrade to bad</summary>
    ManuallyAllowed
}

/// <summary>
///     Reputation data for a single pattern (UA, IP, fingerprint, behavior cluster).
///     Supports online learning AND time-based forgetting.
///     Key properties:
///     - BotScore: 0.0 (human) to 1.0 (bot) - current belief
///     - Support: Effective sample count backing the score
///     - State: Derived from BotScore + Support with hysteresis
///     - LastSeen: For time decay when pattern goes quiet
/// </summary>
public record PatternReputation
{
    /// <summary>Unique pattern identifier (e.g., "ua:hash123", "ip:1.2.3.0/24")</summary>
    public required string PatternId { get; init; }

    /// <summary>Type of pattern: UserAgent, IP, Fingerprint, Behavior, HeaderMix</summary>
    public required string PatternType { get; init; }

    /// <summary>The actual pattern value (regex, CIDR, hash, etc.)</summary>
    public required string Pattern { get; init; }

    /// <summary>Current bot probability [0,1]. 0 = human, 1 = bot, 0.5 = neutral</summary>
    public double BotScore { get; init; } = 0.5;

    /// <summary>Effective sample count - decays over time, increases with observations</summary>
    public double Support { get; init; }

    /// <summary>Current reputation state - determines fast-path behavior</summary>
    public ReputationState State { get; init; } = ReputationState.Neutral;

    /// <summary>When this pattern was first observed</summary>
    public DateTimeOffset FirstSeen { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When this pattern was last observed</summary>
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When state last changed (for audit trail)</summary>
    public DateTimeOffset StateChangedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Whether this pattern was manually set (admin override)</summary>
    public bool IsManual { get; init; }

    /// <summary>Optional notes (e.g., why it was manually blocked)</summary>
    public string? Notes { get; init; }

    /// <summary>
    ///     Confidence in the current BotScore based on support.
    ///     Higher support = higher confidence.
    /// </summary>
    public double Confidence => Math.Min(1.0, Support / 100.0);

    /// <summary>
    ///     Effective weight for fast-path scoring.
    ///     Combines BotScore with State to determine contribution.
    /// </summary>
    public double FastPathWeight => State switch
    {
        ReputationState.ConfirmedBad => Math.Min(BotScore * 0.6, 0.5), // Capped — can't dominate alone
        ReputationState.Suspect => Math.Min(BotScore * 0.3, 0.25), // Moderate bias, never overwhelming
        ReputationState.Neutral => BotScore * 0.05, // Negligible
        ReputationState.ConfirmedGood => -0.2, // Reduces suspicion
        ReputationState.ManuallyBlocked => 1.0, // Always max (admin intent)
        ReputationState.ManuallyAllowed => -1.0, // Always trusted (admin intent)
        _ => 0
    };

    /// <summary>
    ///     Whether this pattern alone can trigger fast-path abort (known bad).
    /// </summary>
    public bool CanTriggerFastAbort => State is ReputationState.ConfirmedBad or ReputationState.ManuallyBlocked;

    /// <summary>
    ///     Whether this pattern alone can trigger fast-path allow (known good).
    ///     Enables instant pass-through for verified search engines, trusted bots, etc.
    /// </summary>
    public bool CanTriggerFastAllow => State is ReputationState.ConfirmedGood or ReputationState.ManuallyAllowed;
}

/// <summary>
///     Configuration for pattern reputation and forgetting.
/// </summary>
public class ReputationOptions
{
    // ==========================================
    // Online Update Settings
    // ==========================================

    /// <summary>
    ///     Learning rate for online updates (EMA alpha).
    ///     Higher = faster adaptation to new evidence.
    ///     Range: 0.01-0.5. Default: 0.1
    /// </summary>
    public double LearningRate { get; set; } = 0.1;

    /// <summary>
    ///     Maximum support value (effective sample cap).
    ///     Prevents ancient evidence from dominating.
    ///     Default: 1000
    /// </summary>
    public double MaxSupport { get; set; } = 1000;

    // ==========================================
    // Time Decay Settings
    // ==========================================

    /// <summary>
    ///     Time constant for BotScore decay toward prior (in hours).
    ///     After τ hours of inactivity, score moves ~63% toward prior.
    ///     Short decay ensures visitors can earn back trust quickly and forces
    ///     the full detector pipeline to re-evaluate instead of relying on stale reputation.
    ///     Default: 3 hours
    /// </summary>
    public double ScoreDecayTauHours { get; set; } = 3;

    /// <summary>
    ///     Time constant for Support decay (in hours).
    ///     After τ hours of inactivity, support drops ~63%.
    ///     When support drops below promotion thresholds, ConfirmedBad demotes
    ///     to Suspect/Neutral, disabling fast-path abort and forcing full detection.
    ///     Default: 6 hours
    /// </summary>
    public double SupportDecayTauHours { get; set; } = 6;

    /// <summary>
    ///     Prior to decay toward when no new evidence arrives.
    ///     0.5 = neutral, <0.5 = bias toward human, >0.5 = bias toward bot.
    ///     Default: 0.5
    /// </summary>
    public double Prior { get; set; } = 0.5;

    /// <summary>
    ///     How often to run the decay sweep (in minutes).
    ///     Must be frequent enough to match the fast score/support decay.
    ///     Default: 15
    /// </summary>
    public int DecaySweepIntervalMinutes { get; set; } = 15;

    // ==========================================
    // Promotion/Demotion Thresholds (Hysteresis)
    // ==========================================

    /// <summary>
    ///     BotScore threshold to promote to ConfirmedBad.
    ///     Default: 0.9
    /// </summary>
    public double PromoteToBadScore { get; set; } = 0.9;

    /// <summary>
    ///     Minimum support to promote to ConfirmedBad.
    ///     Default: 50
    /// </summary>
    public double PromoteToBadSupport { get; set; } = 50;

    /// <summary>
    ///     BotScore threshold to demote from ConfirmedBad to Suspect.
    ///     (Must be lower than PromoteToBadScore for hysteresis)
    ///     Default: 0.7
    /// </summary>
    public double DemoteFromBadScore { get; set; } = 0.7;

    /// <summary>
    ///     Minimum support to demote from ConfirmedBad.
    ///     (Higher than promote = harder to forgive)
    ///     Default: 100
    /// </summary>
    public double DemoteFromBadSupport { get; set; } = 100;

    /// <summary>
    ///     BotScore threshold to promote to Suspect from Neutral.
    ///     Default: 0.6
    /// </summary>
    public double PromoteToSuspectScore { get; set; } = 0.6;

    /// <summary>
    ///     Minimum support to promote to Suspect.
    ///     Default: 10
    /// </summary>
    public double PromoteToSuspectSupport { get; set; } = 10;

    /// <summary>
    ///     BotScore threshold to demote from Suspect to Neutral.
    ///     Default: 0.4
    /// </summary>
    public double DemoteToNeutralScore { get; set; } = 0.4;

    /// <summary>
    ///     BotScore threshold to promote to ConfirmedGood.
    ///     Default: 0.1
    /// </summary>
    public double PromoteToGoodScore { get; set; } = 0.1;

    /// <summary>
    ///     Minimum support to promote to ConfirmedGood.
    ///     Default: 100
    /// </summary>
    public double PromoteToGoodSupport { get; set; } = 100;

    // ==========================================
    // Garbage Collection Settings
    // ==========================================

    /// <summary>
    ///     Days since last seen before pattern is eligible for GC.
    ///     Default: 90
    /// </summary>
    public int GcEligibleDays { get; set; } = 90;

    /// <summary>
    ///     Support threshold below which pattern can be GC'd (if also old).
    ///     Default: 1.0
    /// </summary>
    public double GcSupportThreshold { get; set; } = 1.0;

    /// <summary>
    ///     Only GC patterns in Neutral state.
    ///     Default: true
    /// </summary>
    public bool GcOnlyNeutral { get; set; } = true;
}

/// <summary>
///     Updates pattern reputation based on new evidence and time decay.
///     Encapsulates all the learning and forgetting logic.
/// </summary>
public class PatternReputationUpdater
{
    private readonly ILogger<PatternReputationUpdater> _logger;
    private readonly ReputationOptions _options;

    public PatternReputationUpdater(
        ILogger<PatternReputationUpdater> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value.Reputation;
    }

    /// <summary>
    ///     Apply new evidence to a pattern's reputation.
    ///     Uses exponential moving average for smooth updates.
    /// </summary>
    /// <param name="current">Current reputation (or null for new pattern)</param>
    /// <param name="patternId">Pattern identifier</param>
    /// <param name="patternType">Type of pattern</param>
    /// <param name="pattern">Pattern value</param>
    /// <param name="label">New evidence: 1.0 = bot, 0.0 = human</param>
    /// <param name="evidenceWeight">Weight of this evidence (default 1.0)</param>
    /// <returns>Updated reputation</returns>
    public PatternReputation ApplyEvidence(
        PatternReputation? current,
        string patternId,
        string patternType,
        string pattern,
        double label,
        double evidenceWeight = 1.0)
    {
        var now = DateTimeOffset.UtcNow;

        if (current == null)
        {
            // New pattern - start with the evidence
            var initial = new PatternReputation
            {
                PatternId = patternId,
                PatternType = patternType,
                Pattern = pattern,
                BotScore = label,
                Support = evidenceWeight,
                State = ReputationState.Neutral,
                FirstSeen = now,
                LastSeen = now,
                StateChangedAt = now
            };

            return EvaluateStateChange(initial);
        }

        // Don't update manual overrides
        if (current.IsManual) return current with { LastSeen = now };

        // Apply time decay first (if stale)
        var decayed = ApplyTimeDecay(current);

        // EMA update: new_score = (1 - α) * old_score + α * label
        // Clamp alpha to [0,1] to preserve EMA semantics (alpha > 1 inverts the old score contribution)
        var alpha = Math.Min(_options.LearningRate * evidenceWeight, 1.0);
        var newScore = (1 - alpha) * decayed.BotScore + alpha * label;

        // Increment support (capped)
        var newSupport = Math.Min(decayed.Support + evidenceWeight, _options.MaxSupport);

        var updated = decayed with
        {
            BotScore = Math.Clamp(newScore, 0, 1),
            Support = newSupport,
            LastSeen = now
        };

        return EvaluateStateChange(updated);
    }

    /// <summary>
    ///     Apply time decay to a pattern that hasn't been seen recently.
    ///     Pushes BotScore toward prior and shrinks Support.
    /// </summary>
    public PatternReputation ApplyTimeDecay(PatternReputation reputation)
    {
        if (reputation.IsManual)
            return reputation;

        var hoursSinceLastSeen = (DateTimeOffset.UtcNow - reputation.LastSeen).TotalHours;

        if (hoursSinceLastSeen < 1)
            return reputation; // Too recent to decay

        // Confidence modulates decay speed:
        // High confidence = slower decay (we're sure of this classification)
        // Low confidence = faster decay (uncertain, give benefit of doubt)
        // Scale factor: 0.5 (confidence=0) to 1.0 (confidence=1.0)
        var confidenceScale = 0.5 + reputation.Confidence * 0.5;

        // Score decay toward prior
        // new_score = old_score + (prior - old_score) * (1 - e^(-Δt/τ_eff))
        var effectiveScoreTau = _options.ScoreDecayTauHours * confidenceScale;
        var scoreDecayFactor = 1 - Math.Exp(-hoursSinceLastSeen / effectiveScoreTau);
        var newScore = reputation.BotScore + (_options.Prior - reputation.BotScore) * scoreDecayFactor;

        // Support decay (also confidence-modulated)
        // new_support = old_support * e^(-Δt/τ_eff)
        var effectiveSupportTau = _options.SupportDecayTauHours * confidenceScale;
        var supportDecayFactor = Math.Exp(-hoursSinceLastSeen / effectiveSupportTau);
        var newSupport = reputation.Support * supportDecayFactor;

        var decayed = reputation with
        {
            BotScore = Math.Clamp(newScore, 0, 1),
            Support = newSupport
        };

        return EvaluateStateChange(decayed);
    }

    /// <summary>
    ///     Evaluate whether the pattern's state should change based on current score/support.
    ///     Implements hysteresis to prevent flapping.
    /// </summary>
    public PatternReputation EvaluateStateChange(PatternReputation reputation)
    {
        if (reputation.IsManual)
            return reputation;

        var newState = reputation.State;
        var score = reputation.BotScore;
        var support = reputation.Support;

        switch (reputation.State)
        {
            case ReputationState.Neutral:
                // Can promote to Suspect or ConfirmedGood
                if (score >= _options.PromoteToSuspectScore && support >= _options.PromoteToSuspectSupport)
                    newState = ReputationState.Suspect;
                else if (score <= _options.PromoteToGoodScore && support >= _options.PromoteToGoodSupport)
                    newState = ReputationState.ConfirmedGood;
                break;

            case ReputationState.Suspect:
                // Can promote to ConfirmedBad or demote to Neutral
                if (score >= _options.PromoteToBadScore && support >= _options.PromoteToBadSupport)
                    newState = ReputationState.ConfirmedBad;
                else if (score <= _options.DemoteToNeutralScore || support < _options.PromoteToSuspectSupport)
                    newState = ReputationState.Neutral;
                break;

            case ReputationState.ConfirmedBad:
                // Demote to Suspect when score drops (via new human evidence or time decay).
                // Two paths: (1) enough support to credibly downgrade (high evidence), or
                // (2) support has decayed below the original promotion threshold — the
                // "confirmed" status is no longer supported by sufficient observations.
                if (score <= _options.DemoteFromBadScore &&
                    (support >= _options.DemoteFromBadSupport || support < _options.PromoteToBadSupport))
                    newState = ReputationState.Suspect;
                break;

            case ReputationState.ConfirmedGood:
                // Can demote to Neutral if evidence changes
                if (score >= _options.DemoteToNeutralScore)
                    newState = ReputationState.Neutral;
                break;
        }

        if (newState != reputation.State)
        {
            _logger.LogInformation(
                "Pattern {PatternId} state changed: {OldState} → {NewState} (score={Score:F2}, support={Support:F1})",
                reputation.PatternId, reputation.State, newState, score, support);

            return reputation with
            {
                State = newState,
                StateChangedAt = DateTimeOffset.UtcNow
            };
        }

        return reputation;
    }

    /// <summary>
    ///     Check if a pattern is eligible for garbage collection.
    /// </summary>
    public bool IsEligibleForGc(PatternReputation reputation)
    {
        if (reputation.IsManual)
            return false;

        if (_options.GcOnlyNeutral && reputation.State != ReputationState.Neutral)
            return false;

        var daysSinceLastSeen = (DateTimeOffset.UtcNow - reputation.LastSeen).TotalDays;

        return daysSinceLastSeen >= _options.GcEligibleDays
               && reputation.Support <= _options.GcSupportThreshold;
    }

    /// <summary>
    ///     Manually set a pattern to blocked state (admin override).
    /// </summary>
    public PatternReputation ManuallyBlock(PatternReputation reputation, string? notes = null)
    {
        _logger.LogWarning(
            "Pattern {PatternId} manually blocked{Notes}",
            reputation.PatternId, notes != null ? $": {notes}" : "");

        return reputation with
        {
            State = ReputationState.ManuallyBlocked,
            BotScore = 1.0,
            IsManual = true,
            StateChangedAt = DateTimeOffset.UtcNow,
            Notes = notes
        };
    }

    /// <summary>
    ///     Manually set a pattern to allowed state (admin override).
    /// </summary>
    public PatternReputation ManuallyAllow(PatternReputation reputation, string? notes = null)
    {
        _logger.LogWarning(
            "Pattern {PatternId} manually allowed{Notes}",
            reputation.PatternId, notes != null ? $": {notes}" : "");

        return reputation with
        {
            State = ReputationState.ManuallyAllowed,
            BotScore = 0.0,
            IsManual = true,
            StateChangedAt = DateTimeOffset.UtcNow,
            Notes = notes
        };
    }

    /// <summary>
    ///     Remove manual override, returning to automatic evaluation.
    /// </summary>
    public PatternReputation RemoveManualOverride(PatternReputation reputation)
    {
        if (!reputation.IsManual)
            return reputation;

        _logger.LogInformation(
            "Manual override removed from pattern {PatternId}",
            reputation.PatternId);

        var cleared = reputation with
        {
            IsManual = false,
            State = ReputationState.Neutral,
            StateChangedAt = DateTimeOffset.UtcNow,
            Notes = null
        };

        return EvaluateStateChange(cleared);
    }
}

/// <summary>
///     In-memory cache of pattern reputations with persistence to SQLite.
/// </summary>
public interface IPatternReputationCache
{
    /// <summary>Get reputation for a pattern</summary>
    PatternReputation? Get(string patternId);

    /// <summary>Get or create reputation for a pattern</summary>
    PatternReputation GetOrCreate(string patternId, string patternType, string pattern);

    /// <summary>Update reputation</summary>
    void Update(PatternReputation reputation);

    /// <summary>Get all patterns of a specific type</summary>
    IEnumerable<PatternReputation> GetByType(string patternType);

    /// <summary>Get all patterns in a specific state</summary>
    IEnumerable<PatternReputation> GetByState(ReputationState state);

    /// <summary>Run decay sweep on all patterns</summary>
    Task DecaySweepAsync(CancellationToken ct = default);

    /// <summary>Run garbage collection</summary>
    Task GarbageCollectAsync(CancellationToken ct = default);

    /// <summary>Persist to backing store</summary>
    Task PersistAsync(CancellationToken ct = default);

    /// <summary>Load from backing store</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Get statistics</summary>
    ReputationCacheStats GetStats();
}

/// <summary>
///     Statistics about the reputation cache.
/// </summary>
public class ReputationCacheStats
{
    public int TotalPatterns { get; init; }
    public int NeutralCount { get; init; }
    public int SuspectCount { get; init; }
    public int ConfirmedBadCount { get; init; }
    public int ConfirmedGoodCount { get; init; }
    public int ManualBlockedCount { get; init; }
    public int ManualAllowedCount { get; init; }
    public int GcEligibleCount { get; init; }
    public double AverageBotScore { get; init; }
    public double AverageSupport { get; init; }
    public DateTimeOffset? OldestPattern { get; init; }
    public DateTimeOffset? NewestPattern { get; init; }
    public DateTimeOffset LastDecaySweep { get; init; }
    public DateTimeOffset LastGc { get; init; }
}

/// <summary>
///     In-memory implementation of the pattern reputation cache.
///     Optionally persists to SQLite via ILearnedPatternStore.
/// </summary>
public class InMemoryPatternReputationCache : IPatternReputationCache
{
    private readonly ConcurrentDictionary<string, PatternReputation> _cache = new();
    private readonly ILogger<InMemoryPatternReputationCache> _logger;
    private readonly ILearnedPatternStore? _patternStore;
    private readonly PatternReputationUpdater _updater;
    private DateTimeOffset _lastDecaySweep = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastGc = DateTimeOffset.UtcNow;

    public InMemoryPatternReputationCache(
        ILogger<InMemoryPatternReputationCache> logger,
        PatternReputationUpdater updater,
        ILearnedPatternStore? patternStore = null)
    {
        _logger = logger;
        _updater = updater;
        _patternStore = patternStore;
    }

    public PatternReputation? Get(string patternId)
    {
        return _cache.TryGetValue(patternId, out var rep) ? rep : null;
    }

    public PatternReputation GetOrCreate(string patternId, string patternType, string pattern)
    {
        return _cache.GetOrAdd(patternId, _ => new PatternReputation
        {
            PatternId = patternId,
            PatternType = patternType,
            Pattern = pattern,
            BotScore = 0.5,
            Support = 0,
            State = ReputationState.Neutral,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            StateChangedAt = DateTimeOffset.UtcNow
        });
    }

    public void Update(PatternReputation reputation)
    {
        _cache[reputation.PatternId] = reputation;
    }

    public IEnumerable<PatternReputation> GetByType(string patternType)
    {
        return _cache.Values.Where(r => r.PatternType == patternType);
    }

    public IEnumerable<PatternReputation> GetByState(ReputationState state)
    {
        return _cache.Values.Where(r => r.State == state);
    }

    public Task DecaySweepAsync(CancellationToken ct = default)
    {
        var updated = 0;

        foreach (var (id, rep) in _cache.ToArray())
        {
            if (ct.IsCancellationRequested) break;

            var decayed = _updater.ApplyTimeDecay(rep);
            if (decayed != rep)
            {
                _cache[id] = decayed;
                updated++;
            }
        }

        _lastDecaySweep = DateTimeOffset.UtcNow;

        if (updated > 0) _logger.LogDebug("Decay sweep updated {Count} patterns", updated);

        return Task.CompletedTask;
    }

    public Task GarbageCollectAsync(CancellationToken ct = default)
    {
        var removed = 0;

        foreach (var (id, rep) in _cache.ToArray())
        {
            if (ct.IsCancellationRequested) break;

            if (_updater.IsEligibleForGc(rep))
                if (_cache.TryRemove(id, out _))
                    removed++;
        }

        _lastGc = DateTimeOffset.UtcNow;

        if (removed > 0) _logger.LogInformation("Garbage collected {Count} stale patterns", removed);

        return Task.CompletedTask;
    }

    public async Task PersistAsync(CancellationToken ct = default)
    {
        if (_patternStore == null)
            return;

        var persisted = 0;
        foreach (var reputation in _cache.Values)
        {
            if (ct.IsCancellationRequested) break;

            var signature = new LearnedSignature
            {
                PatternId = reputation.PatternId,
                SignatureType = reputation.PatternType,
                Pattern = reputation.Pattern,
                Confidence = reputation.BotScore,
                Occurrences = (int)Math.Round(reputation.Support),
                FirstSeen = reputation.FirstSeen,
                LastSeen = reputation.LastSeen,
                Action = reputation.State switch
                {
                    ReputationState.ManuallyBlocked => LearnedPatternAction.Full,
                    ReputationState.ConfirmedBad => LearnedPatternAction.Full,
                    ReputationState.Suspect => LearnedPatternAction.ScoreOnly,
                    _ => LearnedPatternAction.LogOnly
                },
                BotType = null,
                BotName = null,
                Source = "InMemoryCache"
            };

            await _patternStore.UpsertAsync(signature, ct);
            persisted++;
        }

        if (persisted > 0)
            _logger.LogInformation("Persisted {Count} patterns to SQLite", persisted);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_patternStore == null)
            return;

        var signatures = await _patternStore.GetByConfidenceAsync(0.0, ct);
        var loaded = 0;

        foreach (var signature in signatures)
        {
            if (ct.IsCancellationRequested) break;

            var state = signature.Action switch
            {
                LearnedPatternAction.Full when signature.Confidence >= 0.9 => ReputationState.ConfirmedBad,
                LearnedPatternAction.Full => ReputationState.Suspect,
                LearnedPatternAction.ScoreOnly when signature.Confidence >= 0.6 => ReputationState.Suspect,
                LearnedPatternAction.LogOnly when signature.Confidence >= 0.9 => ReputationState.ConfirmedBad,
                LearnedPatternAction.LogOnly when signature.Confidence >= 0.6 => ReputationState.Suspect,
                LearnedPatternAction.LogOnly when signature.Confidence <= 0.1 => ReputationState.ConfirmedGood,
                _ => ReputationState.Neutral
            };

            var reputation = new PatternReputation
            {
                PatternId = signature.PatternId,
                PatternType = signature.SignatureType,
                Pattern = signature.Pattern,
                BotScore = signature.Confidence,
                Support = signature.Occurrences,
                State = state,
                FirstSeen = signature.FirstSeen,
                LastSeen = signature.LastSeen,
                StateChangedAt = signature.LastSeen
            };

            _cache[signature.PatternId] = reputation;
            loaded++;
        }

        if (loaded > 0)
            _logger.LogInformation("Loaded {Count} patterns from SQLite", loaded);
    }

    public ReputationCacheStats GetStats()
    {
        var patterns = _cache.Values.ToList();

        return new ReputationCacheStats
        {
            TotalPatterns = patterns.Count,
            NeutralCount = patterns.Count(p => p.State == ReputationState.Neutral),
            SuspectCount = patterns.Count(p => p.State == ReputationState.Suspect),
            ConfirmedBadCount = patterns.Count(p => p.State == ReputationState.ConfirmedBad),
            ConfirmedGoodCount = patterns.Count(p => p.State == ReputationState.ConfirmedGood),
            ManualBlockedCount = patterns.Count(p => p.State == ReputationState.ManuallyBlocked),
            ManualAllowedCount = patterns.Count(p => p.State == ReputationState.ManuallyAllowed),
            GcEligibleCount = patterns.Count(p => _updater.IsEligibleForGc(p)),
            AverageBotScore = patterns.Count > 0 ? patterns.Average(p => p.BotScore) : 0.5,
            AverageSupport = patterns.Count > 0 ? patterns.Average(p => p.Support) : 0,
            OldestPattern = patterns.MinBy(p => p.FirstSeen)?.FirstSeen,
            NewestPattern = patterns.MaxBy(p => p.LastSeen)?.LastSeen,
            LastDecaySweep = _lastDecaySweep,
            LastGc = _lastGc
        };
    }
}