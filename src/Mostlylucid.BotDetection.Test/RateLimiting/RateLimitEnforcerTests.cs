using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.RateLimiting;
using Xunit;

namespace Mostlylucid.BotDetection.Test.RateLimiting;

public class RateLimitEnforcerTests
{
    [Fact]
    public async Task Rule_without_subjects_match_is_skipped()
    {
        var options = new RateLimitOptions
        {
            Limits =
            {
                ["scraper-only"] = new()
                {
                    Subjects =
                    {
                        new RateLimitSubjectSpec { Type = RateLimitSubjectKind.BotType, Value = "Scraper" }
                    },
                    RequestRate = new() { RequestsPerMinute = 60, BurstSize = 1 },
                    OverLimitAction = "block",
                },
            },
        };
        var enforcer = BuildEnforcer(options);
        var registry = new RecordingRegistry();
        var ctx = NewContext();

        // Request is for an AI bot -- the Scraper-only rule shouldn't match,
        // shouldn't consume a token, shouldn't dispatch anything.
        var detection = new DetectionFacts(PrimarySignature: "sig", BotType: "AI", BotName: null, CountryCode: null);
        var outcome = await enforcer.ApplyAsync(ctx, detection, registry, new AggregatedEvidence { BotProbability = 0, Confidence = 0, RiskBand = RiskBand.Unknown });

        Assert.True(outcome.Continue);
        Assert.Empty(registry.Dispatched);
    }

    [Fact]
    public async Task Matching_rule_consumes_one_token_then_blocks_via_OverLimitAction()
    {
        var options = new RateLimitOptions
        {
            Limits =
            {
                ["scraper-cap"] = new()
                {
                    Subjects =
                    {
                        new RateLimitSubjectSpec { Type = RateLimitSubjectKind.BotType, Value = "Scraper" }
                    },
                    RequestRate = new() { RequestsPerMinute = 60, BurstSize = 1 },
                    OverLimitAction = "block",
                },
            },
        };
        var enforcer = BuildEnforcer(options);
        var registry = new RecordingRegistry().With("block", new RecordingActionPolicy("block"));
        var detection = new DetectionFacts("sig", "Scraper", null, null);

        // First request: token available, passes.
        var first = await enforcer.ApplyAsync(NewContext(), detection, registry, new AggregatedEvidence { BotProbability = 0, Confidence = 0, RiskBand = RiskBand.Unknown });
        Assert.True(first.Continue);
        Assert.Empty(registry.Dispatched);

        // Second request: bucket empty, dispatches OverLimitAction.
        var second = await enforcer.ApplyAsync(NewContext(), detection, registry, new AggregatedEvidence { BotProbability = 0, Confidence = 0, RiskBand = RiskBand.Unknown });
        Assert.False(second.Continue);
        Assert.Equal(new[] { "block" }, registry.Dispatched);
    }

    [Fact]
    public async Task Composite_subject_rule_requires_all_predicates_to_match()
    {
        var options = new RateLimitOptions
        {
            Limits =
            {
                ["aggressive-AI-US"] = new()
                {
                    Subjects =
                    {
                        new() { Type = RateLimitSubjectKind.BotType, Value = "AI" },
                        new() { Type = RateLimitSubjectKind.Country, Value = "US" },
                    },
                    RequestRate = new() { RequestsPerMinute = 60, BurstSize = 1 },
                    OverLimitAction = "block",
                },
            },
        };
        var enforcer = BuildEnforcer(options);
        var registry = new RecordingRegistry().With("block", new RecordingActionPolicy("block"));

        // AI + US -> matches, consumes token.
        var ai_us = await enforcer.ApplyAsync(NewContext(),
            new DetectionFacts("sig", "AI", null, "US"), registry, new AggregatedEvidence { BotProbability = 0, Confidence = 0, RiskBand = RiskBand.Unknown });
        Assert.True(ai_us.Continue);

        // AI + GB -> doesn't match (Country mismatch), no consumption.
        var ai_gb = await enforcer.ApplyAsync(NewContext(),
            new DetectionFacts("sig", "AI", null, "GB"), registry, new AggregatedEvidence { BotProbability = 0, Confidence = 0, RiskBand = RiskBand.Unknown });
        Assert.True(ai_gb.Continue);

        // AI + US again -> bucket empty (we consumed our one token above), blocks.
        var ai_us2 = await enforcer.ApplyAsync(NewContext(),
            new DetectionFacts("sig", "AI", null, "US"), registry, new AggregatedEvidence { BotProbability = 0, Confidence = 0, RiskBand = RiskBand.Unknown });
        Assert.False(ai_us2.Continue);
    }

    [Fact]
    public async Task SubjectSpec_Values_list_is_OR()
    {
        var options = new RateLimitOptions
        {
            Limits =
            {
                ["axis-block"] = new()
                {
                    Subjects =
                    {
                        new()
                        {
                            Type = RateLimitSubjectKind.Country,
                            Values = { "RU", "BY" },
                        },
                    },
                    RequestRate = new() { RequestsPerMinute = 60, BurstSize = 1 },
                    OverLimitAction = "block",
                },
            },
        };
        var enforcer = BuildEnforcer(options);
        var registry = new RecordingRegistry().With("block", new RecordingActionPolicy("block"));

        // RU matches the OR list, consumes token. BY would consume the SAME
        // bucket because the composite key is built from the spec, not the
        // matched value -- two visitors from RU and BY share the cap.
        Assert.True((await enforcer.ApplyAsync(NewContext(),
            new DetectionFacts("a", null, null, "RU"), registry, new AggregatedEvidence { BotProbability = 0, Confidence = 0, RiskBand = RiskBand.Unknown })).Continue);

        // CN is not in the OR list -- rule doesn't match.
        Assert.True((await enforcer.ApplyAsync(NewContext(),
            new DetectionFacts("b", null, null, "CN"), registry, new AggregatedEvidence { BotProbability = 0, Confidence = 0, RiskBand = RiskBand.Unknown })).Continue);
    }

    private static RateLimitEnforcer BuildEnforcer(RateLimitOptions options)
    {
        var store = new MemoryRateLimitStateStore();
        var limiter = new MemoryRateLimiter(store);
        var dataRate = new MemoryDataRateLimiter();
        var scopes = new ScopedRateLimitResolver(new TestOptionsMonitor<RateLimitOptions>(options));
        var subjects = new RateLimitSubjectResolver();
        return new RateLimitEnforcer(limiter, dataRate, scopes, subjects);
    }

    private static HttpContext NewContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/";
        ctx.Request.Method = "GET";
        return ctx;
    }

    private sealed class TestOptionsMonitor<T> : IOptions<T> where T : class
    {
        public TestOptionsMonitor(T value) { Value = value; }
        public T Value { get; }
    }

    /// <summary>Bare action-registry stub that just tracks which name was dispatched.</summary>
    private sealed class RecordingRegistry : IActionPolicyRegistry
    {
        private readonly Dictionary<string, IActionPolicy> _byName = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Dispatched { get; } = new();

        public RecordingRegistry With(string name, IActionPolicy policy)
        {
            _byName[name] = policy;
            return this;
        }

        public IActionPolicy? GetPolicy(string name)
        {
            if (!_byName.TryGetValue(name, out var p)) return null;
            Dispatched.Add(name);
            return p;
        }

        public IEnumerable<IActionPolicy> GetPoliciesByType(ActionType type)
            => _byName.Values.Where(p => p.ActionType == type);
        public IReadOnlyDictionary<string, IActionPolicy> GetAllPolicies() => _byName;
        public void RegisterPolicy(IActionPolicy policy) => _byName[policy.Name] = policy;
        public IActionPolicy GetDefaultPolicy(ActionType type)
            => GetPoliciesByType(type).FirstOrDefault()
               ?? throw new InvalidOperationException($"no default for {type}");
    }

    // ── Internal-class exemption (2026-08-16, the compose-path 429 incident) ──

    [Fact]
    public async Task Internal_classified_request_skips_the_rate_limit_rules()
    {
        var options = new RateLimitOptions
        {
            Limits =
            {
                ["sig-limited"] = new()
                {
                    Subjects =
                    {
                        new RateLimitSubjectSpec { Type = RateLimitSubjectKind.Fingerprint, Value = "sig" }
                    },
                    RequestRate = new() { RequestsPerMinute = 1, BurstSize = 1 },
                    OverLimitAction = "block",
                },
            },
        };
        var enforcer = BuildEnforcer(options);
        var registry = new RecordingRegistry();
        var detection = new DetectionFacts(PrimarySignature: "sig", BotType: "Internal", BotName: null, CountryCode: null);
        var normalEvidence = new AggregatedEvidence { BotProbability = 0, Confidence = 0, RiskBand = RiskBand.Unknown };

        // Exhaust the fingerprint bucket with a normal request.
        await enforcer.ApplyAsync(NewContext(), detection, registry, normalEvidence);
        var second = await enforcer.ApplyAsync(NewContext(), detection, registry, normalEvidence);
        Assert.False(second.Continue);

        // Internal-classified (operator-owned) evidence skips the rules entirely —
        // the empty bucket cannot 429 the product's own data path.
        var internalOutcome = await enforcer.ApplyAsync(NewContext(), detection, registry,
            normalEvidence with { PrimaryBotType = BotType.Internal });

        Assert.True(internalOutcome.Continue);
    }

    private sealed class RecordingActionPolicy : IActionPolicy
    {
        public RecordingActionPolicy(string name) { Name = name; }
        public string Name { get; }
        public ActionType ActionType => ActionType.Block;
        public Task<ActionResult> ExecuteAsync(HttpContext context, AggregatedEvidence evidence, CancellationToken ct = default)
            => Task.FromResult(ActionResult.Blocked(429, $"recorded:{Name}"));
    }
}
