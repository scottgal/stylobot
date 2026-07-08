using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.SiteProfiles;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Pins <see cref="EscalateToSessionActionPolicy"/>: the response-side
///     summary escalator that upserts a <see cref="SessionSample"/> into
///     the shared per-domain <see cref="SessionStore"/>. Covers the
///     fingerprint fallback chain, site resolution, threshold gate, and
///     the fact that a policy hit produces exactly one store upsert with
///     the right identity + probability + honeypot bits.
/// </summary>
public class EscalateToSessionActionPolicyTests
{
    private const string PolicyName = "test-session-escalator";
    private const string FingerprintFromItems = "fp-from-items";
    private const string FingerprintFromSignals = "fp-from-signals";
    private const string FingerprintFromRequestSignature = "sig-fallback";

    private static SessionStore NewStore()
    {
        var opts = new SessionStoreOptions { CleanupInterval = TimeSpan.FromHours(1) };
        return new SessionStore(Options.Create(opts), NullLogger<SessionStore>.Instance);
    }

    private static AggregatedEvidence NewEvidence(
        double botProbability = 0.6,
        double confidence = 0.7,
        Dictionary<string, object>? signals = null)
        => new()
        {
            BotProbability = botProbability,
            Confidence = confidence,
            RiskBand = RiskBand.Medium,
            Signals = signals ?? new Dictionary<string, object>(),
        };

    private static HttpContext NewContext(
        Dictionary<object, object?>? items = null,
        string path = "/",
        string method = "GET",
        int responseStatus = 200)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.StatusCode = responseStatus;
        if (items is not null)
            foreach (var (k, v) in items)
                ctx.Items[k] = v;
        return ctx;
    }

    private static EscalateToSessionActionPolicy NewPolicy(
        SessionStore store,
        double minBotProbability = 0.0,
        ISiteProfileResolver? siteProfiles = null)
        => new(
            PolicyName,
            new EscalateToSessionActionOptions { MinBotProbability = minBotProbability },
            store,
            siteProfiles,
            NullLogger<EscalateToSessionActionPolicy>.Instance);

    // ── Threshold gate ──────────────────────────────────────────────

    [Fact]
    public async Task Skips_upsert_when_bot_probability_below_min()
    {
        using var store = NewStore();
        var policy = NewPolicy(store, minBotProbability: 0.5);
        var context = NewContext(items: new()
        {
            [SignalKeys.IdentityFingerprintId] = FingerprintFromItems,
        });
        var evidence = NewEvidence(botProbability: 0.3);

        var result = await policy.ExecuteAsync(context, evidence);

        result.Continue.Should().BeTrue("escalators never block the request");
        result.StatusCode.Should().Be(200);
        result.Description.Should().Contain("below threshold");
        store.TryGet("default", FingerprintFromItems).Should().BeNull(
            "the store must NOT receive a sample when the escalator gate rejects");
    }

    [Fact]
    public async Task Upserts_when_bot_probability_at_or_above_min()
    {
        using var store = NewStore();
        var policy = NewPolicy(store, minBotProbability: 0.5);
        var context = NewContext(items: new()
        {
            [SignalKeys.IdentityFingerprintId] = FingerprintFromItems,
        });
        var evidence = NewEvidence(botProbability: 0.5);

        await policy.ExecuteAsync(context, evidence);

        store.TryGet("default", FingerprintFromItems).Should().NotBeNull();
    }

    // ── Fingerprint fallback chain ──────────────────────────────────

    [Fact]
    public async Task Resolves_fingerprint_from_HttpContext_Items_first()
    {
        using var store = NewStore();
        var policy = NewPolicy(store);
        var context = NewContext(items: new()
        {
            [SignalKeys.IdentityFingerprintId] = FingerprintFromItems,
        });
        // Also populate signals -- the Items path should win over Signals.
        var evidence = NewEvidence(signals: new()
        {
            [SignalKeys.IdentityFingerprintId] = FingerprintFromSignals,
        });

        await policy.ExecuteAsync(context, evidence);

        store.TryGet("default", FingerprintFromItems).Should().NotBeNull();
        store.TryGet("default", FingerprintFromSignals).Should().BeNull();
    }

    [Fact]
    public async Task Falls_back_to_evidence_Signals_when_Items_missing()
    {
        using var store = NewStore();
        var policy = NewPolicy(store);
        var context = NewContext();
        var evidence = NewEvidence(signals: new()
        {
            [SignalKeys.IdentityFingerprintId] = FingerprintFromSignals,
        });

        await policy.ExecuteAsync(context, evidence);

        store.TryGet("default", FingerprintFromSignals).Should().NotBeNull();
    }

    [Fact]
    public async Task Falls_back_to_request_signature_when_no_fingerprint_id()
    {
        using var store = NewStore();
        var policy = NewPolicy(store);
        var context = NewContext();
        var evidence = NewEvidence(signals: new()
        {
            ["request.signature"] = FingerprintFromRequestSignature,
        });

        await policy.ExecuteAsync(context, evidence);

        store.TryGet("default", FingerprintFromRequestSignature).Should().NotBeNull(
            "L1 signature is the last-resort fallback when no learned fingerprint has resolved yet");
    }

    [Fact]
    public async Task No_op_when_no_fingerprint_resolvable()
    {
        using var store = NewStore();
        var policy = NewPolicy(store);
        var context = NewContext();
        var evidence = NewEvidence(); // no signals, no items

        var raises = 0;
        store.Changes.TypedSignalRaised += _ => raises++;

        var result = await policy.ExecuteAsync(context, evidence);

        result.Description.Should().Contain("no fingerprint");
        raises.Should().Be(0, "no fingerprint means no upsert and no change fired");
    }

    // ── Site resolution ──────────────────────────────────────────────

    [Fact]
    public async Task Uses_default_site_when_no_resolver_registered()
    {
        using var store = NewStore();
        var policy = NewPolicy(store, siteProfiles: null);
        var context = NewContext(items: new()
        {
            [SignalKeys.IdentityFingerprintId] = FingerprintFromItems,
        });

        await policy.ExecuteAsync(context, NewEvidence());

        store.TryGet("default", FingerprintFromItems).Should().NotBeNull(
            "hosts without a site resolver partition into a single default site");
    }

    [Fact]
    public async Task Partitions_by_resolved_site_id()
    {
        using var store = NewStore();
        var resolver = new FakeSiteResolver(new SiteProfile { Id = "site-a" });
        var policy = NewPolicy(store, siteProfiles: resolver);
        var context = NewContext(items: new()
        {
            [SignalKeys.IdentityFingerprintId] = FingerprintFromItems,
        });

        await policy.ExecuteAsync(context, NewEvidence());

        store.TryGet("site-a", FingerprintFromItems).Should().NotBeNull();
        store.TryGet("default", FingerprintFromItems).Should().BeNull();
    }

    [Fact]
    public async Task Falls_back_to_default_site_when_resolver_returns_null()
    {
        using var store = NewStore();
        var resolver = new FakeSiteResolver(null);
        var policy = NewPolicy(store, siteProfiles: resolver);
        var context = NewContext(items: new()
        {
            [SignalKeys.IdentityFingerprintId] = FingerprintFromItems,
        });

        await policy.ExecuteAsync(context, NewEvidence());

        store.TryGet("default", FingerprintFromItems).Should().NotBeNull(
            "unmatched host must partition into default rather than get discarded");
    }

    // ── Sample content ──────────────────────────────────────────────

    [Fact]
    public async Task Records_honeypot_hit_when_signal_present()
    {
        using var store = NewStore();
        var policy = NewPolicy(store);
        var context = NewContext(
            items: new() { [SignalKeys.IdentityFingerprintId] = FingerprintFromItems });
        var evidence = NewEvidence(signals: new()
        {
            ["request.honeypot"] = true,
        });

        await policy.ExecuteAsync(context, evidence);

        var aggregate = store.TryGet("default", FingerprintFromItems);
        aggregate.Should().NotBeNull();
        aggregate!.HoneypotHits.Should().Be(1);
    }

    [Fact]
    public async Task Marks_from_upstream_false_when_stylobot_synthesised()
    {
        using var store = NewStore();
        var policy = NewPolicy(store);
        var context = NewContext(
            items: new() { [SignalKeys.IdentityFingerprintId] = FingerprintFromItems },
            responseStatus: 403);
        var evidence = NewEvidence(signals: new()
        {
            [SignalKeys.ResponseFromUpstream] = false,
        });

        await policy.ExecuteAsync(context, evidence);

        var aggregate = store.TryGet("default", FingerprintFromItems);
        aggregate!.UpstreamStatusCounts.Should().NotContainKey(403,
            "stylobot's own enforcement code must not be counted upstream (closed-loop guard)");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private sealed class FakeSiteResolver : ISiteProfileResolver
    {
        private readonly SiteProfile? _profile;
        public FakeSiteResolver(SiteProfile? profile) => _profile = profile;
        public SiteProfile? Resolve(HttpContext context) => _profile;
        public SiteProfile? ResolveByHost(string host) => _profile;
    }
}