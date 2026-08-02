using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Enforcement;

/// <summary>
///     2026-08-02 fp-cache-current architecture: enforcement must read the SAME live
///     fingerprint score (<see cref="Fingerprint.CachedBotProbability"/>) the dashboard
///     headline reads, not a stale per-request <see cref="AggregatedEvidence.BotProbability"/>
///     -- otherwise the two surfaces can disagree about the same visitor (the exact class of
///     bug this rebuild exists to close). <see cref="BotDetectionOrchestrator"/> already
///     writes this request's verdict into the fingerprint cache (power-weighted absorption)
///     BEFORE <see cref="PostDetectionActionGate.EvaluateAsync"/> runs, so a same-request
///     read-back sees the freshly-absorbed value.
///
///     Falls back to <see cref="AggregatedEvidence.BotProbability"/> under exactly three
///     conditions: Identity disabled (store is <see cref="NullFingerprintStore"/>, always
///     resolves null), no fingerprint id resolved this request, or the request is
///     learning-suppressed (<c>IsLearningSuppressedByApiKey</c>) -- the last one because a
///     learning-suppressed request must score AND enforce purely on its own evidence, never
///     read (or influence) another request's absorbed history.
/// </summary>
public sealed class PostDetectionActionGateFingerprintScoreTests
{
    private const string FingerprintId = "fp-enforcement-test";

    [Fact]
    public async Task LiveFingerprintScore_BelowThreshold_SuppressesEnforcement_EvenWhenPerRequestScoreIsHigh()
    {
        var store = new FakeFingerprintStore(_ => (FingerprintId, 0.10));
        var gate = Gate(store);
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, _) = await gate.EvaluateAsync(
            context, Evidence(perRequestBotProbability: 0.95), registry);

        Assert.Equal(PostDetectionActionOutcome.NoOverride, outcome);
        Assert.Empty(registry.Requested);
    }

    [Fact]
    public async Task LiveFingerprintScore_AboveThreshold_TriggersEnforcement_EvenWhenPerRequestScoreIsLow()
    {
        var store = new FakeFingerprintStore(_ => (FingerprintId, 0.95));
        var gate = Gate(store);
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, evidence) = await gate.EvaluateAsync(
            context, Evidence(perRequestBotProbability: 0.10), registry);

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
        Assert.Equal("throttle-tools", evidence.TriggeredActionPolicyName);
    }

    [Fact]
    public async Task NoFingerprintId_FallsBackToPerRequestScore()
    {
        var store = new FakeFingerprintStore(_ => (FingerprintId, 0.10));
        var gate = Gate(store);
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, _) = await gate.EvaluateAsync(
            context, Evidence(perRequestBotProbability: 0.95, fingerprintId: null), registry);

        // No fingerprint id resolved this request -- must enforce on the per-request score,
        // not silently pass every unidentified visitor.
        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
    }

    [Fact]
    public async Task IdentityDisabled_NullFingerprintStore_FallsBackToPerRequestScore()
    {
        // NullFingerprintStore is the FOSS default (Identity:Enabled = false): every lookup
        // resolves null, so enforcement must fall back to the per-request score rather than
        // treating an always-null read as "always below threshold".
        var gate = Gate(new NullFingerprintStore());
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, _) = await gate.EvaluateAsync(
            context, Evidence(perRequestBotProbability: 0.95), registry);

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
    }

    [Fact]
    public async Task LearningSuppressed_FallsBackToPerRequestScore_IgnoringLiveFingerprintScore()
    {
        // A learning-suppressed request (DisableLearningWrites key) must not have its
        // enforcement decision driven by another request's absorbed fingerprint history --
        // it scores and enforces purely on its own evidence.
        var store = new FakeFingerprintStore(_ => (FingerprintId, 0.95));
        var gate = Gate(store);
        var registry = new RecordingRegistry();
        var context = Context();
        context.Items["BotDetection.ApiKeyContext"] = new ApiKeyContext
        {
            KeyName = "suppressed-key",
            DisabledDetectors = Array.Empty<string>(),
            WeightOverrides = new Dictionary<string, double>(),
            DisableLearningWrites = true
        };

        var (outcome, _) = await gate.EvaluateAsync(
            context, Evidence(perRequestBotProbability: 0.10), registry);

        // Live score (0.95) is ignored; per-request score (0.10) is below threshold.
        Assert.Equal(PostDetectionActionOutcome.NoOverride, outcome);
    }

    [Fact]
    public async Task FingerprintNotResident_FallsBackToPerRequestScore()
    {
        // Store resolves the id but returns null (race / not yet resident this request).
        var store = new FakeFingerprintStore(_ => null);
        var gate = Gate(store);
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, _) = await gate.EvaluateAsync(
            context, Evidence(perRequestBotProbability: 0.95), registry);

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
    }

    private static PostDetectionActionGate Gate(IFingerprintStore store) => new(
        Options.Create(new BotDetectionOptions
        {
            BotThreshold = 0.70,
            BotTypeActionPolicies = new Dictionary<string, string>
            {
                ["Tool"] = "throttle-tools"
            }
        }),
        NullLogger<PostDetectionActionGate>.Instance,
        fingerprintStore: store);

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/some/path";
        return context;
    }

    private static AggregatedEvidence Evidence(double perRequestBotProbability, string? fingerprintId = FingerprintId) => new()
    {
        BotProbability = perRequestBotProbability,
        Confidence = 1.0,
        RiskBand = RiskBand.High,
        PrimaryBotType = BotType.Tool,
        PrimaryBotName = "curl",
        FingerprintId = fingerprintId,
        Signals = new Dictionary<string, object>()
    };

    /// <summary>Minimal fingerprint constructor covering only the fields these tests assert on.</summary>
    private static Fingerprint FakeFingerprint(string id, double cachedBotProbability)
    {
        var now = DateTime.UtcNow;
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = Array.Empty<float>(),
            CentroidMaturity = 1,
            Weights = Array.Empty<float>(),
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            CachedBotProbability = cachedBotProbability
        };
    }

    private sealed class FakeFingerprintStore : NullFingerprintStore
    {
        private readonly Func<string, (string Id, double CachedBotProbability)?> _resolve;

        public FakeFingerprintStore(Func<string, (string Id, double CachedBotProbability)?> resolve)
            => _resolve = resolve;

        public override Task<Fingerprint?> GetFingerprintAsync(string fingerprintId, CancellationToken ct = default)
        {
            var resolved = _resolve(fingerprintId);
            return Task.FromResult(resolved is null
                ? null
                : FakeFingerprint(resolved.Value.Id, resolved.Value.CachedBotProbability));
        }
    }

    private sealed class RecordingRegistry : IActionPolicyRegistry
    {
        public List<string> Requested { get; } = new();

        public IActionPolicy? GetPolicy(string name)
        {
            Requested.Add(name);
            return new StubPolicy(name);
        }

        public IEnumerable<IActionPolicy> GetPoliciesByType(ActionType type) => Array.Empty<IActionPolicy>();
        public IReadOnlyDictionary<string, IActionPolicy> GetAllPolicies() => new Dictionary<string, IActionPolicy>();
        public void RegisterPolicy(IActionPolicy policy) { }
        public IActionPolicy GetDefaultPolicy(ActionType type) => new StubPolicy("default");
    }

    private sealed class StubPolicy : IActionPolicy
    {
        public StubPolicy(string name) => Name = name;
        public string Name { get; }
        public ActionType ActionType => ActionType.Throttle;

        public Task<ActionResult> ExecuteAsync(
            HttpContext context, AggregatedEvidence evidence, CancellationToken cancellationToken = default)
            => Task.FromResult(ActionResult.Allowed());
    }
}
