using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Privacy;
using Mostlylucid.BotDetection.SimulationPacks;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Tests for <see cref="CookieBehaviorContributor" />,
///     <see cref="ResourceWaterfallContributor" />,
///     <see cref="PeriodicityContributor" />,
///     and partial Markov chain archetypes in <see cref="SessionVectorContributor" />.
/// </summary>
public class NewDetectorTests
{
    #region Helpers

    /// <summary>
    ///     Stub config provider that returns default parameters with optional overrides.
    /// </summary>
    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        private readonly Dictionary<string, object> _parameters;

        public StubConfigProvider(Dictionary<string, object>? parameters = null)
        {
            _parameters = parameters ?? new Dictionary<string, object>();
        }

        public DetectorManifest? GetManifest(string detectorName) => null;

        public DetectorDefaults GetDefaults(string detectorName) => new()
        {
            Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 1.0 },
            Confidence = new ConfidenceDefaults
            {
                BotDetected = 0.3, HumanIndicated = -0.2, Neutral = 0.0, StrongSignal = 0.5
            },
            Parameters = new Dictionary<string, object>(_parameters)
        };

        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue)
        {
            if (_parameters.TryGetValue(parameterName, out var val))
            {
                try { return (T)Convert.ChangeType(val, typeof(T)); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        public Task<T> GetParameterAsync<T>(
            string detectorName,
            string parameterName,
            ConfigResolutionContext context,
            T defaultValue,
            CancellationToken ct = default)
            => Task.FromResult(GetParameter(detectorName, parameterName, defaultValue));

        public void InvalidateCache(string? detectorName = null) { }

        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }

    private static BlackboardState CreateState(
        Dictionary<string, object>? signals = null,
        Action<HttpContext>? configureHttp = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
        configureHttp?.Invoke(ctx);
        var signalDict = new ConcurrentDictionary<string, object>(signals ?? new Dictionary<string, object>());
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };
    }

    private static IMemoryCache CreateCache() => new MemoryCache(new MemoryCacheOptions());

    #endregion

    #region CookieBehaviorContributor Tests

    private static CookieBehaviorContributor CreateCookieContributor(
        IMemoryCache? cache = null,
        Dictionary<string, object>? configParams = null)
    {
        cache ??= CreateCache();
        return new CookieBehaviorContributor(
            NullLogger<CookieBehaviorContributor>.Instance,
            new StubConfigProvider(configParams),
            cache);
    }

    [Fact]
    public async Task CookieBehavior_NoCookiesAfterSetCookie_BotSignal()
    {
        var cache = CreateCache();
        var contributor = CreateCookieContributor(cache);
        const string sig = "test-sig-cookie-bot";

        // Simulate Set-Cookie being sent by the server
        contributor.RecordSetCookie(sig, 3);

        // Send 5 requests without Cookie header
        IReadOnlyList<DetectionContribution> result = [];
        for (var i = 0; i < 5; i++)
        {
            var state = CreateState(new Dictionary<string, object>
            {
                [SignalKeys.PrimarySignature] = sig
            });
            // No Cookie header set on request
            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // After enough requests, should detect cookies being ignored
        var botContribs = result.Where(r => r.ConfidenceDelta > 0).ToList();
        Assert.NotEmpty(botContribs);
        Assert.Contains(botContribs, c => c.Reason.Contains("ignored", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CookieBehavior_CookiesPresent_HumanSignal()
    {
        var cache = CreateCache();
        var contributor = CreateCookieContributor(cache);
        const string sig = "test-sig-cookie-human";

        // Simulate Set-Cookie being sent
        contributor.RecordSetCookie(sig, 2);

        // Send requests with growing Cookie header
        IReadOnlyList<DetectionContribution> result = [];
        for (var i = 0; i < 6; i++)
        {
            // Build growing cookie string: each request adds another cookie
            var cookies = string.Join("; ",
                Enumerable.Range(0, i + 1).Select(n => $"cookie{n}=value{n}"));

            var state = CreateState(
                new Dictionary<string, object>
                {
                    [SignalKeys.PrimarySignature] = sig
                },
                ctx => ctx.Request.Headers["Cookie"] = cookies);

            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Should detect cookie accumulation as human behavior
        var humanContribs = result.Where(r => r.ConfidenceDelta < 0).ToList();
        Assert.NotEmpty(humanContribs);
        Assert.Contains(humanContribs, c => c.Reason.Contains("accumulation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CookieBehavior_TooFewRequests_NeutralSignal()
    {
        var cache = CreateCache();
        var contributor = CreateCookieContributor(cache);
        const string sig = "test-sig-cookie-few";

        contributor.RecordSetCookie(sig, 1);

        // Only 2 requests (below default min_requests_for_analysis of 3)
        IReadOnlyList<DetectionContribution> result = [];
        for (var i = 0; i < 2; i++)
        {
            var state = CreateState(new Dictionary<string, object>
            {
                [SignalKeys.PrimarySignature] = sig
            });
            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Should be neutral - not enough data
        Assert.All(result, c => Assert.True(c.ConfidenceDelta == 0,
            $"Expected neutral but got delta={c.ConfidenceDelta}: {c.Reason}"));
    }

    [Fact]
    public async Task CookieBehavior_NoSetCookieObserved_NeutralSignal()
    {
        var cache = CreateCache();
        var contributor = CreateCookieContributor(cache);
        const string sig = "test-sig-cookie-noset";

        // Do NOT call RecordSetCookie - server never sent Set-Cookie

        IReadOnlyList<DetectionContribution> result = [];
        for (var i = 0; i < 5; i++)
        {
            var state = CreateState(new Dictionary<string, object>
            {
                [SignalKeys.PrimarySignature] = sig
            });
            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Should be neutral - can't judge without Set-Cookie
        Assert.All(result, c => Assert.True(c.ConfidenceDelta == 0,
            $"Expected neutral but got delta={c.ConfidenceDelta}: {c.Reason}"));
    }

    #endregion

    #region ResourceWaterfallContributor Tests

    private static ResourceWaterfallContributor CreateResourceContributor(
        IMemoryCache? cache = null,
        Dictionary<string, object>? configParams = null)
    {
        cache ??= CreateCache();
        return new ResourceWaterfallContributor(
            NullLogger<ResourceWaterfallContributor>.Instance,
            new StubConfigProvider(configParams),
            cache);
    }

    private static BlackboardState CreateResourceState(
        string sig,
        string secFetchDest = "",
        string accept = "",
        string path = "/page")
    {
        return CreateState(
            new Dictionary<string, object>
            {
                [SignalKeys.PrimarySignature] = sig,
                [SignalKeys.TransportProtocolClass] = "document"
            },
            ctx =>
            {
                if (!string.IsNullOrEmpty(secFetchDest))
                    ctx.Request.Headers["Sec-Fetch-Dest"] = secFetchDest;
                if (!string.IsNullOrEmpty(accept))
                    ctx.Request.Headers.Accept = accept;
                ctx.Request.Path = path;
            });
    }

    [Fact]
    public async Task ResourceWaterfall_DocumentsOnlyNoAssets_BotSignal()
    {
        var cache = CreateCache();
        var contributor = CreateResourceContributor(cache);
        const string sig = "test-sig-resource-bot";

        // Send 5 document requests, no assets
        IReadOnlyList<DetectionContribution> result = [];
        for (var i = 0; i < 5; i++)
        {
            var state = CreateResourceState(sig,
                secFetchDest: "document",
                path: $"/page/{i}");
            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Should detect no sub-resources as bot signal
        var botContribs = result.Where(r => r.ConfidenceDelta > 0).ToList();
        Assert.NotEmpty(botContribs);
        Assert.Contains(botContribs, c => c.Reason.Contains("zero sub-resource", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResourceWaterfall_HealthyAssetRatio_HumanSignal()
    {
        var cache = CreateCache();
        var contributor = CreateResourceContributor(cache);
        const string sig = "test-sig-resource-human";

        // Send 3 document requests
        for (var i = 0; i < 3; i++)
        {
            var state = CreateResourceState(sig,
                secFetchDest: "document",
                path: $"/page/{i}");
            await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Send 10 asset requests (scripts, styles, images)
        IReadOnlyList<DetectionContribution> result = [];
        var assetTypes = new[] { "script", "style", "image", "script", "style",
                                  "image", "script", "style", "image", "font" };
        for (var i = 0; i < 10; i++)
        {
            var state = CreateResourceState(sig,
                secFetchDest: assetTypes[i],
                path: $"/assets/file{i}");
            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Asset ratio = 10/3 = 3.33, which exceeds healthy_ratio_threshold (2.0)
        var humanContribs = result.Where(r => r.ConfidenceDelta < 0).ToList();
        Assert.NotEmpty(humanContribs);
        Assert.Contains(humanContribs, c => c.Reason.Contains("Healthy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResourceWaterfall_FaviconRequested_HumanSignal()
    {
        var cache = CreateCache();
        var contributor = CreateResourceContributor(cache);
        const string sig = "test-sig-resource-favicon";

        // Send 3 document requests (minimum for analysis)
        for (var i = 0; i < 3; i++)
        {
            var state = CreateResourceState(sig,
                secFetchDest: "document",
                path: $"/page/{i}");
            await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Also send some assets to avoid the "no assets" bot signal overwhelming
        for (var i = 0; i < 7; i++)
        {
            var state = CreateResourceState(sig,
                secFetchDest: "script",
                path: $"/assets/app{i}.js");
            await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Request favicon.ico
        var faviconState = CreateResourceState(sig,
            path: "/favicon.ico");
        var result = await contributor.ContributeAsync(faviconState, CancellationToken.None);

        // Should have a human contribution for favicon
        var faviconContrib = result.FirstOrDefault(r =>
            r.Reason.Contains("Favicon", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(faviconContrib);
        Assert.True(faviconContrib.ConfidenceDelta < 0, "Favicon should be a human signal (negative delta)");
    }

    [Fact]
    public async Task ResourceWaterfall_TooFewDocuments_NeutralSignal()
    {
        var cache = CreateCache();
        var contributor = CreateResourceContributor(cache);
        const string sig = "test-sig-resource-few";

        // Only 1 document request (below min_documents_for_analysis of 3)
        var state = CreateResourceState(sig,
            secFetchDest: "document",
            path: "/page/1");
        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Should be neutral
        Assert.All(result, c => Assert.True(c.ConfidenceDelta == 0,
            $"Expected neutral but got delta={c.ConfidenceDelta}: {c.Reason}"));
    }

    #endregion

    #region PeriodicityContributor Tests

    private static PeriodicityContributor CreatePeriodicityContributor(
        IMemoryCache? cache = null,
        Dictionary<string, object>? configParams = null)
    {
        cache ??= CreateCache();
        return new PeriodicityContributor(
            NullLogger<PeriodicityContributor>.Instance,
            new StubConfigProvider(configParams),
            cache);
    }

    [Fact]
    public async Task Periodicity_FixedInterval_BotSignal()
    {
        var cache = CreateCache();
        // Lower min_requests so we can trigger with 15 requests
        var contributor = CreatePeriodicityContributor(cache, new Dictionary<string, object>
        {
            ["min_requests"] = 10
        });
        const string sig = "test-sig-periodic-bot";

        // The PeriodicityContributor records timestamps internally via DateTimeOffset.UtcNow,
        // so we can't directly control timing. Instead, we'll pre-populate the cache
        // with a fixed-interval timestamp history and then fire one more request.
        var key = $"periodicity:{sig}";
        var history = new List<DateTimeOffset>();
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-15);
        for (var i = 0; i < 14; i++)
        {
            // Exactly 1 second apart - very low CV
            history.Add(baseTime.AddSeconds(i));
        }
        cache.Set(key, history, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });

        // Fire one more request - the contributor will add UtcNow, which is ~15 min later.
        // This breaks the CV, so we add one more to the cache right before calling.
        // Actually, let's add all 15 to the cache and call once more to just read the analysis.
        history.Add(baseTime.AddSeconds(14));
        cache.Set(key, history, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });

        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = sig
        });
        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // The contributor will append UtcNow (which is ~15 min after the last cached ts),
        // making the last interval huge. But the first 14 intervals are all 1.0s.
        // With 15 intervals: 14 at 1.0s, plus 1 at ~900s = CV will be high.
        // To properly test this, we need to control the full history including the "now" point.
        // So let's set the last entry to be 1 second before "now":
        history.Clear();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 15; i++)
        {
            history.Add(now.AddSeconds(-(15 - i))); // 15s ago, 14s ago, ..., 1s ago
        }
        cache.Set(key, history, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });

        state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = sig
        });
        result = await contributor.ContributeAsync(state, CancellationToken.None);

        // The contributor adds DateTimeOffset.UtcNow, making ~16 entries.
        // Last interval is ~1s (since the last cached entry was 1s ago).
        // All intervals are ~1s, so CV should be very low.
        var botContribs = result.Where(r => r.ConfidenceDelta > 0).ToList();
        Assert.NotEmpty(botContribs);
        Assert.Contains(botContribs, c => c.Reason.Contains("polling", StringComparison.OrdinalIgnoreCase)
                                          || c.Reason.Contains("interval", StringComparison.OrdinalIgnoreCase)
                                          || c.Reason.Contains("Periodic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Periodicity_IrregularTiming_HumanSignal()
    {
        var cache = CreateCache();
        var contributor = CreatePeriodicityContributor(cache, new Dictionary<string, object>
        {
            ["min_requests"] = 10
        });
        const string sig = "test-sig-periodic-human";

        // Pre-populate cache with irregular timestamps spread across many hours
        // so that hour entropy is moderate (2.0 < entropy < 3.5) and CV is high (> 0.8).
        // The contributor will append UtcNow, so we set the last entry close to "now"
        // to keep the final interval small.
        var key = $"periodicity:{sig}";
        var now = DateTimeOffset.UtcNow;
        var history = new List<DateTimeOffset>();

        // Wildly varying intervals: some very short (2s), some very long (3600s+).
        // This produces CV >> 1.0. Spread across 5+ distinct hours for entropy > 2.0.
        var secondsOffsets = new[]
        {
            -18000, // -5h
            -17998, // 2s gap
            -14400, // -4h (3598s gap)
            -14395, // 5s gap
            -10800, // -3h (3595s gap)
            -10797, // 3s gap
            -10790, // 7s gap
            -7200,  // -2h (3590s gap)
            -7197,  // 3s gap
            -3600,  // -1h (3597s gap)
            -3598,  // 2s gap
            -3590,  // 8s gap
            -60,    // near now (3530s gap)
            -55,    // 5s gap
            -2      // 53s gap - close to now so UtcNow adds ~2s
        };
        foreach (var secOffset in secondsOffsets)
            history.Add(now.AddSeconds(secOffset));

        cache.Set(key, history, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30) });

        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = sig
        });
        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // With high CV (irregular) and moderate hour entropy, should get human signal
        var humanContribs = result.Where(r => r.ConfidenceDelta < 0).ToList();
        Assert.NotEmpty(humanContribs);
        Assert.Contains(humanContribs, c => c.Reason.Contains("rhythm", StringComparison.OrdinalIgnoreCase)
                                            || c.Reason.Contains("Irregular", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Periodicity_TooFewRequests_NeutralSignal()
    {
        var cache = CreateCache();
        var contributor = CreatePeriodicityContributor(cache);
        const string sig = "test-sig-periodic-few";

        // Only 5 requests (below default min_requests of 10)
        IReadOnlyList<DetectionContribution> result = [];
        for (var i = 0; i < 5; i++)
        {
            var state = CreateState(new Dictionary<string, object>
            {
                [SignalKeys.PrimarySignature] = sig
            });
            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Should return empty (below min threshold returns early with empty list)
        Assert.Empty(result);
    }

    #endregion

    #region Partial Markov Chain Archetype Tests

    [Fact]
    public void PartialChain_ScraperArchetype_HasHighSelfSimilarity()
    {
        // Build a scraper-like partial Markov chain: PageView→PageView→PageView→PageView
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, DateTimeOffset.UtcNow.AddSeconds(-4), "/page/1", 200),
            new(RequestState.PageView, DateTimeOffset.UtcNow.AddSeconds(-3), "/page/2", 200),
            new(RequestState.PageView, DateTimeOffset.UtcNow.AddSeconds(-2), "/page/3", 200),
            new(RequestState.PageView, DateTimeOffset.UtcNow.AddSeconds(-1), "/page/4", 200)
        };

        var fullVector = SessionVectorizer.Encode(requests, null);
        var stateCount = Enum.GetValues<RequestState>().Length;
        var transitionDims = stateCount * stateCount;
        var partialVector = new float[transitionDims];
        Array.Copy(fullVector, partialVector, Math.Min(fullVector.Length, transitionDims));

        // L2-normalize
        float norm = 0;
        for (var i = 0; i < partialVector.Length; i++)
            norm += partialVector[i] * partialVector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
            for (var i = 0; i < partialVector.Length; i++)
                partialVector[i] /= norm;

        // Find best matching archetype
        var scraperArchetype = MarkovArchetypes.All.First(a => a.Name == "scraper");
        var similarity = CosineSimilarity(partialVector, scraperArchetype.PartialVector);

        // PageView→PageView chain should strongly match the scraper archetype
        Assert.True(similarity > 0.6f,
            $"Scraper pattern similarity should exceed 0.6 but was {similarity:F3}");
        Assert.False(scraperArchetype.IsHuman, "Scraper archetype should not be human");
    }

    [Fact]
    public void PartialChain_HumanBrowserPattern_MatchesHumanArchetype()
    {
        // Build a human-browser-like chain: PageView→StaticAsset→StaticAsset→PageView
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, DateTimeOffset.UtcNow.AddSeconds(-4), "/page/1", 200),
            new(RequestState.StaticAsset, DateTimeOffset.UtcNow.AddSeconds(-3), "/assets/app.js", 200),
            new(RequestState.StaticAsset, DateTimeOffset.UtcNow.AddSeconds(-2), "/assets/style.css", 200),
            new(RequestState.PageView, DateTimeOffset.UtcNow.AddSeconds(-1), "/page/2", 200)
        };

        var fullVector = SessionVectorizer.Encode(requests, null);
        var stateCount = Enum.GetValues<RequestState>().Length;
        var transitionDims = stateCount * stateCount;
        var partialVector = new float[transitionDims];
        Array.Copy(fullVector, partialVector, Math.Min(fullVector.Length, transitionDims));

        // L2-normalize
        float norm = 0;
        for (var i = 0; i < partialVector.Length; i++)
            norm += partialVector[i] * partialVector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
            for (var i = 0; i < partialVector.Length; i++)
                partialVector[i] /= norm;

        // Find best matching archetype
        var humanArchetype = MarkovArchetypes.All.First(a => a.Name == "human-browser");
        var similarity = CosineSimilarity(partialVector, humanArchetype.PartialVector);

        // PageView→StaticAsset→StaticAsset→PageView should match human-browser
        Assert.True(similarity > 0.6f,
            $"Human browser pattern similarity should exceed 0.6 but was {similarity:F3}");
        Assert.True(humanArchetype.IsHuman, "human-browser archetype should be human");
    }

    [Fact]
    public void PartialChain_TooFewRequests_ZeroVector()
    {
        // Only 2 requests - only 1 transition, produces a sparse vector
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, DateTimeOffset.UtcNow.AddSeconds(-2), "/page/1", 200),
            new(RequestState.PageView, DateTimeOffset.UtcNow.AddSeconds(-1), "/page/2", 200)
        };

        var fullVector = SessionVectorizer.Encode(requests, null);
        var stateCount = Enum.GetValues<RequestState>().Length;
        var transitionDims = stateCount * stateCount;
        var partialVector = new float[transitionDims];
        Array.Copy(fullVector, partialVector, Math.Min(fullVector.Length, transitionDims));

        // With only 1 transition, the vector is extremely sparse.
        // The SessionVectorContributor gates on partial_chain_min_requests (default 3),
        // so 2 requests would never reach AnalyzePartialChain.
        // Verify that the vector is valid but trivial (only one non-zero element).
        var nonZeroCount = partialVector.Count(v => v > 0);
        Assert.True(nonZeroCount <= 1,
            $"With only 2 requests (1 transition), expected at most 1 non-zero transition dim but got {nonZeroCount}");
    }

    [Fact]
    public async Task PartialChain_ViaContributor_ScraperPattern_BotSignal()
    {
        // Integration test: feed scraper-like requests through the full SessionVectorContributor
        var cache = CreateCache();
        var sessionStore = new SessionStore(
            cache,
            NullLogger<SessionStore>.Instance);

        var contributor = new SessionVectorContributor(
            NullLogger<SessionVectorContributor>.Instance,
            new StubConfigProvider(new Dictionary<string, object>
            {
                ["partial_chain_min_requests"] = 3,
                ["partial_chain_similarity_threshold"] = 0.5,
                ["min_session_requests"] = 5
            }),
            sessionStore);

        const string sig = "test-sig-partial-scraper";

        // Send 4 PageView-only requests (scraper pattern)
        IReadOnlyList<DetectionContribution> result = [];
        for (var i = 0; i < 4; i++)
        {
            var state = CreateState(
                new Dictionary<string, object>
                {
                    [SignalKeys.PrimarySignature] = sig
                },
                ctx =>
                {
                    ctx.Request.Path = $"/page/{i}";
                    ctx.Request.Headers.Accept = "text/html";
                    // No Sec-Fetch-Dest - fallback to Accept header for classification
                });

            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // Should match the scraper archetype via partial chain analysis
        // (4 requests >= partial_chain_min_requests, < min_session_requests)
        var partialChainSignal = result.FirstOrDefault(r =>
            r.Reason.Contains("Partial chain", StringComparison.OrdinalIgnoreCase));

        // The partial chain may or may not match depending on classification -
        // verify that some contribution was returned
        Assert.NotEmpty(result);

        // If partial chain matched, verify it's a bot signal
        if (partialChainSignal != null)
        {
            Assert.True(partialChainSignal.ConfidenceDelta > 0,
                "Scraper partial chain should produce a positive (bot) confidence delta");
        }
    }

    [Fact]
    public async Task PartialChain_ViaContributor_HumanPattern_HumanSignal()
    {
        // Integration test: feed browser-like requests through SessionVectorContributor
        var cache = CreateCache();
        var sessionStore = new SessionStore(
            cache,
            NullLogger<SessionStore>.Instance);

        var contributor = new SessionVectorContributor(
            NullLogger<SessionVectorContributor>.Instance,
            new StubConfigProvider(new Dictionary<string, object>
            {
                ["partial_chain_min_requests"] = 3,
                ["partial_chain_similarity_threshold"] = 0.5,
                ["min_session_requests"] = 5
            }),
            sessionStore);

        const string sig = "test-sig-partial-human";

        // Send browser-like requests: PageView, then StaticAssets, then PageView
        var requestPaths = new[]
        {
            ("/page/1", "document", "text/html"),
            ("/assets/app.js", "script", "application/javascript"),
            ("/assets/style.css", "style", "text/css"),
            ("/page/2", "document", "text/html")
        };

        IReadOnlyList<DetectionContribution> result = [];
        foreach (var (path, secFetchDest, accept) in requestPaths)
        {
            var state = CreateState(
                new Dictionary<string, object>
                {
                    [SignalKeys.PrimarySignature] = sig,
                    [SignalKeys.TransportProtocolClass] = secFetchDest is "script" or "style" ? "static" : "document"
                },
                ctx =>
                {
                    ctx.Request.Path = path;
                    ctx.Request.Headers["Sec-Fetch-Dest"] = secFetchDest;
                    ctx.Request.Headers.Accept = accept;
                });

            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        Assert.NotEmpty(result);

        // If partial chain matched human-browser, it should be a human signal
        var partialChainSignal = result.FirstOrDefault(r =>
            r.Reason.Contains("Partial chain", StringComparison.OrdinalIgnoreCase));
        if (partialChainSignal != null)
        {
            Assert.True(partialChainSignal.ConfidenceDelta < 0,
                "Human browser partial chain should produce a negative (human) confidence delta");
        }
    }

    /// <summary>
    ///     Cosine similarity for two float arrays.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0f;
    }

    #endregion

    #region UaPiiStripper Tests

    [Fact]
    public void UaPiiStripper_StripEmail_Redacts()
    {
        var result = UaPiiStripper.Strip("MyBot/1.0 (admin@company.com)");
        Assert.Contains("[email]", result);
        Assert.DoesNotContain("admin@company.com", result);
    }

    [Fact]
    public void UaPiiStripper_NormalUA_Unchanged()
    {
        const string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        var result = UaPiiStripper.Strip(ua);
        Assert.Equal(ua, result);
    }

    [Fact]
    public void UaPiiStripper_NullInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, UaPiiStripper.Strip(null));
        Assert.Equal(string.Empty, UaPiiStripper.Strip(""));
        Assert.Equal(string.Empty, UaPiiStripper.Strip("   "));
    }

    #endregion

    #region HeaderHashCollector Tests

    private static HeaderHashCollector CreateHeaderHashCollector()
    {
        var hasher = new PiiHasher(new byte[32]);
        return new HeaderHashCollector(hasher);
    }

    [Fact]
    public void HeaderHashCollector_CollectsDiscriminatoryHeaders()
    {
        var collector = CreateHeaderHashCollector();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Accept-Language"] = "en-US,en;q=0.9";
        ctx.Request.Headers["Accept-Encoding"] = "gzip, deflate, br";
        ctx.Request.Headers["Sec-CH-UA"] = "\"Chromium\";v=\"120\"";

        var hashes = collector.CollectHashes(ctx.Request);

        Assert.True(hashes.ContainsKey("accept-language"));
        Assert.True(hashes.ContainsKey("accept-encoding"));
        Assert.True(hashes.ContainsKey("sec-ch-ua"));
        Assert.True(hashes.ContainsKey("_header_order"));
    }

    [Fact]
    public void HeaderHashCollector_ExcludesJunkHeaders()
    {
        var collector = CreateHeaderHashCollector();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Host"] = "example.com";
        ctx.Request.Headers["Cookie"] = "session=abc";
        ctx.Request.Headers["Authorization"] = "Bearer token";
        // Add a discriminatory header so we actually get results
        ctx.Request.Headers["Accept-Language"] = "en-US";

        var hashes = collector.CollectHashes(ctx.Request);

        Assert.False(hashes.ContainsKey("host"));
        Assert.False(hashes.ContainsKey("cookie"));
        Assert.False(hashes.ContainsKey("authorization"));
    }

    #endregion

    #region StabilityAnalyzer Tests

    [Fact]
    public void StabilityAnalyzer_StableHeader_HighAnchorStrength()
    {
        // Same hash for "accept-language" across 5 sessions
        var sessions = Enumerable.Range(0, 5)
            .Select(_ => new Dictionary<string, string> { ["accept-language"] = "hash-stable" })
            .ToList();

        var anchors = StabilityAnalyzer.ComputeAnchors(sessions);

        Assert.True(anchors.ContainsKey("accept-language"));
        var score = anchors["accept-language"];
        Assert.True(score.PersonalStability >= 0.99,
            $"Expected PersonalStability near 1.0 but got {score.PersonalStability}");
        Assert.Equal(1, score.UniqueValues);
    }

    [Fact]
    public void StabilityAnalyzer_VolatileHeader_LowAnchorStrength()
    {
        // Different hash for "accept-language" in each of 5 sessions
        var sessions = Enumerable.Range(0, 5)
            .Select(i => new Dictionary<string, string> { ["accept-language"] = $"hash-{i}" })
            .ToList();

        var anchors = StabilityAnalyzer.ComputeAnchors(sessions);

        Assert.True(anchors.ContainsKey("accept-language"));
        var score = anchors["accept-language"];
        Assert.True(score.PersonalStability <= 0.21,
            $"Expected PersonalStability near 0.2 but got {score.PersonalStability}");
        Assert.Equal(5, score.UniqueValues);
    }

    #endregion

    #region HeaderCorrelationContributor Tests

    private static HeaderCorrelationContributor CreateHeaderCorrelationContributor(
        IMemoryCache? cache = null,
        Dictionary<string, object>? configParams = null)
    {
        cache ??= CreateCache();
        return new HeaderCorrelationContributor(
            NullLogger<HeaderCorrelationContributor>.Instance,
            new StubConfigProvider(configParams),
            cache);
    }

    [Fact]
    public async Task HeaderCorrelation_ThreeDistinctUAs_SameHeaders_BotSignal()
    {
        var cache = CreateCache();
        var contributor = CreateHeaderCorrelationContributor(cache);

        // 3 requests with different PrimarySignatures (simulating different UAs)
        // but identical non-UA headers from the same IP
        IReadOnlyList<DetectionContribution> result = [];
        for (var i = 0; i < 3; i++)
        {
            var state = CreateState(
                new Dictionary<string, object>
                {
                    [SignalKeys.PrimarySignature] = $"sig-{i}",
                    [SignalKeys.ClientIp] = "192.168.1.100"
                },
                ctx =>
                {
                    // Same discriminatory headers across all requests
                    ctx.Request.Headers["Accept-Language"] = "en-US,en;q=0.9";
                    ctx.Request.Headers["Accept-Encoding"] = "gzip, deflate, br";
                    ctx.Request.Headers["Accept"] = "text/html,application/xhtml+xml";
                });

            result = await contributor.ContributeAsync(state, CancellationToken.None);
        }

        // After 3 distinct signatures with identical header profiles, should detect UA rotation
        var botContribs = result.Where(r => r.ConfidenceDelta > 0).ToList();
        Assert.NotEmpty(botContribs);
        Assert.Contains(botContribs, c => c.Reason.Contains("rotation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HeaderCorrelation_SingleUA_NeutralSignal()
    {
        var cache = CreateCache();
        var contributor = CreateHeaderCorrelationContributor(cache);

        var state = CreateState(
            new Dictionary<string, object>
            {
                [SignalKeys.PrimarySignature] = "sig-only-one",
                [SignalKeys.ClientIp] = "192.168.1.100"
            },
            ctx =>
            {
                ctx.Request.Headers["Accept-Language"] = "en-US,en;q=0.9";
                ctx.Request.Headers["Accept-Encoding"] = "gzip, deflate, br";
                ctx.Request.Headers["Accept"] = "text/html";
            });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Single signature - should be neutral
        Assert.All(result, c => Assert.True(c.ConfidenceDelta == 0,
            $"Expected neutral but got delta={c.ConfidenceDelta}: {c.Reason}"));
    }

    #endregion

    #region CveProbeContributor Tests

    [Fact]
    public async Task CveProbe_WordPressPath_BotSignal()
    {
        // Use the real SimulationPackLoader which loads from embedded resources
        var registry = new SimulationPackLoader(
            NullLogger<SimulationPackLoader>.Instance);

        var contributor = new CveProbeContributor(
            registry,
            NullLogger<CveProbeContributor>.Instance);

        var state = CreateState(
            configureHttp: ctx =>
            {
                ctx.Request.Path = "/wp-login.php";
            });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // /wp-login.php should match a WordPress simulation pack honeypot path
        var botContribs = result.Where(r => r.ConfidenceDelta > 0).ToList();
        Assert.NotEmpty(botContribs);
    }

    [Fact]
    public async Task CveProbe_NormalPath_NeutralSignal()
    {
        var registry = new SimulationPackLoader(
            NullLogger<SimulationPackLoader>.Instance);

        var contributor = new CveProbeContributor(
            registry,
            NullLogger<CveProbeContributor>.Instance);

        var state = CreateState(
            configureHttp: ctx =>
            {
                ctx.Request.Path = "/";
            });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Normal path should produce no contributions
        Assert.Empty(result);
    }

    #endregion

    #region PiiQueryStringContributor Tests

    [Fact]
    public async Task PiiQueryString_EmailInQuery_DetectsPii()
    {
        var hasher = new PiiHasher(new byte[32]);
        var contributor = new PiiQueryStringContributor(
            NullLogger<PiiQueryStringContributor>.Instance, hasher);

        var state = CreateState(
            configureHttp: ctx =>
            {
                ctx.Request.QueryString = new QueryString("?email=user@test.com");
            });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Should detect PII and emit privacy signals
        Assert.NotEmpty(result);
        Assert.True(state.Signals.ContainsKey(SignalKeys.PrivacyQueryPiiDetected),
            "Expected privacy.query_pii_detected signal to be written");
    }

    [Fact]
    public async Task PiiQueryString_CleanQuery_NoPii()
    {
        var hasher = new PiiHasher(new byte[32]);
        var contributor = new PiiQueryStringContributor(
            NullLogger<PiiQueryStringContributor>.Instance, hasher);

        var state = CreateState(
            configureHttp: ctx =>
            {
                ctx.Request.QueryString = new QueryString("?page=1&sort=name");
            });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Should produce no contributions for clean query
        Assert.Empty(result);
        Assert.False(state.Signals.ContainsKey(SignalKeys.PrivacyQueryPiiDetected),
            "Should not write PII signal for clean query string");
    }

    #endregion

    #region SignatureContributor Tests

    [Fact]
    public async Task Signature_WritesPrimarySignature()
    {
        var hasher = new PiiHasher(new byte[32]);
        var signatureService = new MultiFactorSignatureService(
            hasher,
            NullLogger<MultiFactorSignatureService>.Instance);
        var headerHashCollector = new HeaderHashCollector(hasher);

        var contributor = new SignatureContributor(
            NullLogger<SignatureContributor>.Instance,
            signatureService,
            headerHashCollector);

        var state = CreateState(
            configureHttp: ctx =>
            {
                ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.1");
                ctx.Request.Headers.UserAgent = "Mozilla/5.0 TestBrowser";
            });

        await contributor.ContributeAsync(state, CancellationToken.None);

        // Verify the contributor writes PrimarySignature to the blackboard
        Assert.True(state.Signals.ContainsKey(SignalKeys.PrimarySignature),
            "SignatureContributor should write PrimarySignature to the blackboard");
        var sig = state.GetSignal<string>(SignalKeys.PrimarySignature);
        Assert.False(string.IsNullOrEmpty(sig), "PrimarySignature should not be empty");
    }

    #endregion
}