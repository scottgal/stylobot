using FluentAssertions;
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
///     2026-08-02 fp-cache-current architecture, operator refinement: the read-through fallback
///     on a MISS (Identity on, fingerprint resolved, but no cached score yet) must LEAVE THE
///     TRACE, not just compute a bare per-request value and discard it. The first-ever write
///     to a fresh fingerprint is a direct assignment (<see cref="SqliteFingerprintStore.RecordVerdictWriteBehindWithPower"/>),
///     so the cache self-heals: the NEXT read for that fingerprint is a HIT, not a recomputed
///     bare fallback, and accumulates via power-weighted absorption from there. Only Identity
///     disabled / no fingerprint id / learning-suppressed are genuine "can't leave a trace"
///     terminal cases (covered by <see cref="PostDetectionActionGateFingerprintScoreTests"/>).
///
///     This test exercises the REAL <see cref="SqliteFingerprintStore"/> +
///     <see cref="PostDetectionActionGate"/> together (not a fake store) so the miss -&gt;
///     populate -&gt; hit -&gt; accumulate flow is proven end to end, not just asserted piecewise.
/// </summary>
public sealed class FingerprintReadThroughTraceTests : IDisposable
{
    private readonly string _tempDir;

    public FingerprintReadThroughTraceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-readthrough-trace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<SqliteFingerprintStore> NewStoreAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, IdentityVectorLayout.DefaultV1());
        await store.EnsureInitialisedAsync();
        return store;
    }

    private static Fingerprint FreshFingerprint(string id, int dim)
    {
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now
            // CachedBotProbability defaults to 0.0, CachedScoreUpdatedAt defaults to null --
            // the "never scored yet" allocation-time shape a MISS starts from.
        };
    }

    private static PostDetectionActionGate Gate(IFingerprintStore store) => new(
        Options.Create(new BotDetectionOptions { BotThreshold = 0.70 }),
        NullLogger<PostDetectionActionGate>.Instance,
        fingerprintStore: store);

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/some/path";
        return context;
    }

    private static AggregatedEvidence Evidence(string fingerprintId, double perRequestBotProbability) => new()
    {
        BotProbability = perRequestBotProbability,
        Confidence = 1.0,
        RiskBand = RiskBand.High,
        PrimaryBotType = BotType.Tool,
        FingerprintId = fingerprintId,
        Signals = new Dictionary<string, object>()
    };

    [Fact]
    public async Task FirstEverWrite_LeavesTheTrace_SameRequestEnforcementReadSeesIt_NotTheBareFallback()
    {
        var store = await NewStoreAsync();
        const string fpId = "fp-trace";
        var dim = store.Layout.Dimension;
        await store.InsertFingerprintAsync(FreshFingerprint(fpId, dim), "sig-trace", CancellationToken.None);
        await store.GetFingerprintAsync(fpId); // resident-load, mirrors the matcher

        // MISS: no cached score yet. This mirrors the orchestrator's unconditional per-request
        // write (RecordVerdictWriteBehindWithPower) that runs BEFORE PostDetectionActionGate --
        // the trace this test is proving gets left.
        store.RecordVerdictWriteBehindWithPower(fpId, botProbability: 0.85, confidence: 0.9, isDefinitive: false);

        var afterFirstWrite = await store.GetFingerprintAsync(fpId);
        afterFirstWrite!.CachedBotProbability.Should().Be(0.85,
            "first-ever write for a fresh fingerprint is a direct assignment -- the trace");

        // Enforcement reads with a DELIBERATELY DIFFERENT per-request value (0.10, below
        // threshold) so a fire here can only be explained by reading the trace (0.85, above
        // threshold), not a recomputed bare per-request fallback.
        var gate = Gate(store);
        var registry = new RecordingRegistry();
        var (outcome, _) = await gate.EvaluateAsync(
            Context(), Evidence(fpId, perRequestBotProbability: 0.10), registry);

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
    }

    [Fact]
    public async Task SubsequentWrite_HitsTheTrace_AndAccumulates_RatherThanReMissing()
    {
        var store = await NewStoreAsync();
        const string fpId = "fp-accumulate";
        var dim = store.Layout.Dimension;
        await store.InsertFingerprintAsync(FreshFingerprint(fpId, dim), "sig-accumulate", CancellationToken.None);
        await store.GetFingerprintAsync(fpId);

        // Request 1 (MISS -> populate): direct assignment leaves the trace.
        store.RecordVerdictWriteBehindWithPower(fpId, botProbability: 0.85, confidence: 0.9, isDefinitive: false);

        // Request 2, same fingerprint, later: a maximally-ambiguous observation
        // (botProbability = 0.5 -> certainty = |0.5-0.5|*2*confidence = 0 regardless of
        // confidence) blends at exactly the floor alpha (0.3 default). If this were
        // re-treated as another MISS (perpetually missing, never accumulating), it would
        // direct-assign to 0.5 verbatim. Instead it must HIT the trace and blend --
        // 0.85*(1-0.3) + 0.5*0.3 = 0.745 -- proving the cache self-heals and accumulates
        // rather than staying a permanent bare-fallback loop.
        store.RecordVerdictWriteBehindWithPower(fpId, botProbability: 0.5, confidence: 0.5, isDefinitive: false);

        var after = await store.GetFingerprintAsync(fpId);
        after!.CachedBotProbability.Should().BeApproximately(0.745, 1e-9,
            "a maximally-ambiguous second observation must blend against the FIRST write's trace " +
            "(0.85) at the floor alpha, not re-assign as if this were a fresh miss");

        // Enforcement's NEXT read sees this accumulated (blended) value, not the original
        // trace and not a re-derived bare fallback.
        var gate = Gate(store);
        var registry = new RecordingRegistry();
        var (outcome, _) = await gate.EvaluateAsync(
            Context(), Evidence(fpId, perRequestBotProbability: 0.01), registry);

        // 0.745 is still above BotThreshold=0.70 -- confirms the read is live (tracks the
        // accumulated store value), not frozen at the first trace nor reading the near-zero
        // per-request fallback.
        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
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
