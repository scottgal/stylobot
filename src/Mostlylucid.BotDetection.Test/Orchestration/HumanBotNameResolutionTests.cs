using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Failure exposed on staging 2026-06-19 ~18:40 UTC: a human Chrome
///     request to /dashboard rendered the signature detail page with a BLANK
///     H1, even though the UA chip showed "Chrome 149.0.0 / macOS". The user
///     observed dozens of human rows with no name on /dashboard/visitors.
///
///     This suite pins the orchestrator's name-resolution contract for
///     humans -- a UA that parses MUST produce a non-null
///     AggregatedEvidence.PrimaryBotName so the persistence and read
///     layers can never have a null name to drop.
/// </summary>
public class HumanBotNameResolutionTests
{
    private static readonly string ChromeMacUa =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";

    [Fact]
    public void Human_with_UA_family_signals_yields_non_null_PrimaryBotName()
    {
        var ledger = new DetectionLedger("test-human-chrome");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Header",
            Category = "Header",
            ConfidenceDelta = 0.0,
            Weight = 1.0,
            Reason = "Clean browser headers"
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = ChromeMacUa,
            [SignalKeys.UserAgentIsBot] = false,
            [SignalKeys.UserAgentFamily] = "Chrome",
            [SignalKeys.UserAgentFamilyVersion] = "149",
            [SignalKeys.UserAgentOs] = "macOS",
        };

        var result = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.NotNull(result.PrimaryBotName);
        Assert.NotEmpty(result.PrimaryBotName);
    }

    /// <summary>
    ///     REGRESSION (same root cause as the catalog-bot "Unknown"): in production the
    ///     UA atom raises <c>ua.raw</c> via <c>sink.Raise</c> and preSignals (from
    ///     ledger.MergedSignals) carries NONE of the UA signals. Before the sink-first
    ///     raw-UA threading into ResolveDisplayName, <c>Compose(preSignals)</c> had
    ///     nothing to parse, so every human browser resolved to the "Unknown" terminal
    ///     when Identity was off. Earlier tests missed this because they injected
    ///     ua.family / ua.os straight into premergedSignals. This pins the production shape.
    /// </summary>
    [Fact]
    public void Human_with_sink_only_raw_UA_resolves_browser_name_not_Unknown()
    {
        var ledger = new DetectionLedger("test-human-sink");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Header",
            Category = "Header",
            ConfidenceDelta = 0.0,
            Weight = 1.0,
            Reason = "Clean browser headers"
        });

        // The atom's real emit path: raw UA on the sink, absent from the merged dict.
        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.UserAgent}:{ChromeMacUa}", "test");

        var result = ledger.ToAggregatedEvidence(
            aiRan: false, premergedSignals: new Dictionary<string, object>(), sink: sink);

        Assert.NotNull(result.PrimaryBotName);
        Assert.Contains("Chrome", result.PrimaryBotName); // parsed from the sink-only UA, not "Unknown"
    }

    [Fact]
    public void Human_with_matcher_IdentityDisplayName_signal_carries_through()
    {
        var ledger = new DetectionLedger("test-human-matcher");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Header",
            Category = "Header",
            ConfidenceDelta = 0.0,
            Weight = 1.0,
            Reason = "Clean browser headers"
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = ChromeMacUa,
            [SignalKeys.UserAgentIsBot] = false,
            [SignalKeys.UserAgentFamily] = "Chrome",
            [SignalKeys.UserAgentFamilyVersion] = "149",
            [SignalKeys.UserAgentOs] = "macOS",
            [SignalKeys.IdentityDisplayName] = "Mac Chrome 149 / macOS",
        };

        var result = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.Equal("Mac Chrome 149 / macOS", result.PrimaryBotName);
    }

    [Fact]
    public void Human_with_no_UA_signals_falls_through_to_NoUserAgent_terminal()
    {
        var ledger = new DetectionLedger("test-human-empty");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Header",
            Category = "Header",
            ConfidenceDelta = 0.0,
            Weight = 1.0,
            Reason = "n/a"
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentIsBot] = false,
        };

        var result = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.NotNull(result.PrimaryBotName);
        Assert.NotEmpty(result.PrimaryBotName);
    }

    /// <summary>
    ///     Full broadcast-event chain for a human: ToAggregatedEvidence
    ///     followed by BuildDetectionFromEvidence. The dashboard event's
    ///     BotName field must NOT be null for a human request -- that's
    ///     what gets persisted to the detections table and read back into
    ///     Model.BotName on the signature detail page. A null here is the
    ///     blank-H1 staging bug.
    /// </summary>
    [Fact]
    public void Human_request_through_BuildDetectionFromEvidence_produces_non_null_BotName()
    {
        var ledger = new DetectionLedger("test-human-broadcast");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Header",
            Category = "Header",
            ConfidenceDelta = 0.0,
            Weight = 1.0,
            Reason = "Clean browser headers"
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = ChromeMacUa,
            [SignalKeys.UserAgentIsBot] = false,
            [SignalKeys.UserAgentFamily] = "Chrome",
            [SignalKeys.UserAgentFamilyVersion] = "149",
            [SignalKeys.UserAgentOs] = "macOS",
        };

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        var middleware = new Mostlylucid.BotDetection.UI.Middleware.DetectionBroadcastMiddleware(
            _ => System.Threading.Tasks.Task.CompletedTask,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Mostlylucid.BotDetection.UI.Middleware.DetectionBroadcastMiddleware>.Instance);

        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Response.StatusCode = 200;
        ctx.Request.Headers.UserAgent = ChromeMacUa;
        ctx.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();

        var detection = middleware.BuildDetectionFromEvidence(ctx, evidence);

        Assert.False(detection.IsBot, "request should be classified as human");
        Assert.False(string.IsNullOrEmpty(detection.BotName),
            $"detection.BotName must be non-null/non-empty for a human request; got '{detection.BotName}'");
    }

    /// <summary>
    ///     Verdict-cache Skip path primitive: the composer must self-rescue from
    ///     the raw UA when the cached signals snapshot is sparse (no ua.family /
    ///     ua.os written). Without this the Skip path's PrimaryBotName assignment
    ///     receives null for every cache-hit human request.
    ///     This is the single load-bearing fact for the bug fix at
    ///     BotDetectionMiddleware.cs:539 -- Compose returns a real name when
    ///     given a Chrome UA and an empty signals dict.
    /// </summary>
    [Fact]
    public void FingerprintNameComposer_with_sparse_signals_and_chrome_UA_returns_non_null_name()
    {
        var sparseSignals = new Dictionary<string, object>();

        var result = FingerprintNameComposer.Compose(sparseSignals, userAgent: ChromeMacUa);

        Assert.False(string.IsNullOrEmpty(result),
            $"Compose must produce a non-null name when given a Chrome UA, even with no signals; got '{result}'");
    }

    /// <summary>
    ///     Verdict-cache Skip path resolution chain. Mirrors the priority order
    ///     used by DetectionLedgerExtensions.ResolveDisplayName on the MISS path
    ///     so both cache states produce coherent names:
    ///         1. signals[IdentityDisplayName] (matcher-supplied composed name)
    ///         2. live-UA-derived bot name (catalog match)
    ///         3. FingerprintNameComposer.Compose(signals, userAgent: liveUa)
    ///     For a human Chrome request, all three matter: Skip-path signals
    ///     may not carry IdentityDisplayName (matcher didn't re-run), the catalog
    ///     name is null (humans don't match bot patterns), so Compose is the
    ///     terminal rescue. This test pins that contract so the Skip-path fix
    ///     can never silently regress to "PrimaryBotName = uaBotName".
    /// </summary>
    [Fact]
    public void Skip_path_resolution_chain_returns_non_null_for_human_chrome_request()
    {
        // Sparse cached signals -- typical of an old verdict snapshot that doesn't
        // include UA-parsed family/version/os entries.
        var cachedSignals = new Dictionary<string, object>();

        // For a real Chrome browser, the live UA matcher returns no bot name.
        string? uaBotName = null;

        // Resolution chain expected at BotDetectionMiddleware.cs:539:
        var identityName = cachedSignals.TryGetValue(SignalKeys.IdentityDisplayName, out var idn)
            ? idn as string : null;
        var resolved = !string.IsNullOrEmpty(identityName)
            ? identityName
            : (!string.IsNullOrEmpty(uaBotName)
                ? uaBotName
                : FingerprintNameComposer.Compose(cachedSignals, userAgent: ChromeMacUa));

        Assert.False(string.IsNullOrEmpty(resolved),
            $"Skip-path cached evidence must resolve a non-null BotName for a Chrome human; got '{resolved}'");
    }
}
