using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.StyloExtract.Actions;
using Mostlylucid.BotDetection.StyloExtract.ContentCache;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

/// <summary>
///     Content-cache policies implement <see cref="IPolicyStateContributor"/> so the dashboard
///     policy tab can render their effective state (representation, match, cache mode, bounds,
///     counters) without the core referencing the pack. These tests pin the contributed shape
///     through the real <see cref="RegistryPolicyStateProvider"/>.
/// </summary>
public sealed class ContentCachePolicyStateTests
{
    [Fact]
    public void ContentCachePolicy_ContributesRepresentationMatchModeAndBounds()
    {
        var (registry, telemetry) = BuildRegistry();

        var state = new RegistryPolicyStateProvider(registry).Get("content-cache-search");

        state.Should().NotBeNull();
        state!.EffectiveParams["representation"].Should().Be("Html");
        state.EffectiveParams["match"].Should().Be("all traffic routed to this policy");
        state.EffectiveParams["cacheMode"].Should().Be("enabled");
        state.EffectiveParams["maxEntries"].Should().Be(128);
        state.EffectiveParams["maxEntryBytes"].Should().Be(256 * 1024);
        state.EffectiveParams["maxTotalBytes"].Should().Be(32 * 1024 * 1024);
        state.EffectiveParams["slidingExpiration"].Should().Be("00:02:00");
        state.EffectiveParams["absoluteExpiration"].Should().Be("00:15:00");
        state.EffectiveParams["versionSalt"].Should().Be("v1");
        state.EffectiveParams["hits"].Should().Be(0L);
        state.EffectiveParams["overrides"].Should().Be(0L);
    }

    [Fact]
    public void MarkdownPolicy_MatchDescribesAiGateAndOverride()
    {
        var (registry, _) = BuildRegistry();

        var state = new RegistryPolicyStateProvider(registry).Get("extract-markdown-cache-ai");

        state.Should().NotBeNull();
        state!.EffectiveParams["representation"].Should().Be("Markdown");
        state.EffectiveParams["match"].ToString().Should()
            .Contain("AiBot").And.Contain("markdown=true");
    }

    [Fact]
    public void Counters_AreReflectedInTheContributedParams()
    {
        var (registry, telemetry) = BuildRegistry();

        telemetry.Hit("content-cache-search");
        telemetry.Bypass("content-cache-search");
        telemetry.Override("extract-markdown-cache-ai");

        var state = new RegistryPolicyStateProvider(registry).Get("content-cache-search");

        state!.EffectiveParams["hits"].Should().Be(1L);
        state.EffectiveParams["bypasses"].Should().Be(1L);

        var md = new RegistryPolicyStateProvider(registry).Get("extract-markdown-cache-ai");
        md!.EffectiveParams["overrides"].Should().Be(1L);
    }

    [Fact]
    public void DisabledCache_PolicyReportsCacheModeDisabled()
    {
        var options = new StyloExtractActionOptions(); // TransformedContentCache.Enabled defaults false
        var policy = new ContentCacheSearchActionPolicy(
            new StaticOptions(options),
            NullLogger<ContentCacheSearchActionPolicy>.Instance,
            new ResponseBodyCapture(),
            new CacheControlWriter(),
            new MarkdownResponseCache(options.TransformedContentCache),
            new CacheKeyBuilder(),
            new CacheabilityEvaluator(),
            new ContentCacheTelemetry());
        var registry = RegistryWith(policy);

        var state = new RegistryPolicyStateProvider(registry).Get("content-cache-search");

        state!.EffectiveParams["cacheMode"].Should().Be("disabled");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static (ActionPolicyRegistry Registry, ContentCacheTelemetry Telemetry) BuildRegistry()
    {
        var telemetry = new ContentCacheTelemetry();

        var searchOptions = new StyloExtractActionOptions
        {
            TransformedContentCache = new TransformedContentCacheOptions { Enabled = true }
        };
        var search = new ContentCacheSearchActionPolicy(
            new StaticOptions(searchOptions),
            NullLogger<ContentCacheSearchActionPolicy>.Instance,
            new ResponseBodyCapture(),
            new CacheControlWriter(),
            new MarkdownResponseCache(searchOptions.TransformedContentCache),
            new CacheKeyBuilder(),
            new CacheabilityEvaluator(),
            telemetry);

        var mdOptions = new StyloExtractActionOptions
        {
            TransformedContentCache = new TransformedContentCacheOptions { Enabled = true }
        };
        var md = new ExtractMarkdownCacheAiActionPolicy(
            new FakeExtractor(),
            new StaticOptions(mdOptions),
            NullLogger<ExtractMarkdownCacheAiActionPolicy>.Instance,
            new ResponseBodyCapture(),
            new CacheControlWriter(),
            new MarkdownResponseCache(mdOptions.TransformedContentCache),
            new CacheKeyBuilder(),
            new CacheabilityEvaluator(),
            telemetry);

        return (RegistryWith(search, md), telemetry);
    }

    private static ActionPolicyRegistry RegistryWith(params IActionPolicy[] policies)
        => new(
            Microsoft.Extensions.Options.Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>(),
            policies);
}
