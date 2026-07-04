using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that detects bots which ignore
///     Set-Cookie headers. HTTP libraries (Python requests, Go net/http, Node
///     axios, curl) typically discard Set-Cookie entirely; real browsers always
///     handle cookies.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>CookieBehaviorContributor</c>. Tracks per-signature:
///         Set-Cookie count (from previous responses), returned-cookie count,
///         total request count. Zero-PII: only counts are tracked, never
///         cookie names or values.
///     </para>
///     <para>
///         Cross-request state in <see cref="IMemoryCache"/>. Priority 20 --
///         after boundary sensors, before behavioural analysis. Requires
///         <see cref="SignalKeys.PrimarySignature"/> to have been raised.
///     </para>
///     <para>
///         <see cref="RecordSetCookie"/> is invoked from the response pipeline
///         when the gateway emits <c>Set-Cookie</c> so this atom can compare
///         issuance vs. return on the next request. External contract
///         preserved from the legacy contributor.
///     </para>
/// </remarks>
public sealed class CookieBehaviorAtom : DetectorAtomBase
{
    private readonly ILogger<CookieBehaviorAtom> _logger;
    private readonly IMemoryCache _cache;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private const string CachePrefix = "cookiebehavior:";

    public CookieBehaviorAtom(
        ILogger<CookieBehaviorAtom> logger,
        IDetectorConfigProvider configProvider,
        IMemoryCache cache,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "CookieBehavior", category: "CookieBehavior")
    {
        _logger = logger;
        _cache = cache;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 20;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.PrimarySignature };

    // Config-driven thresholds
    private int MinRequestsForAnalysis => _configProvider.GetParameter(Name, "min_requests_for_analysis", 3);
    private double CookieIgnoredConfidence => _configProvider.GetParameter(Name, "cookie_ignored_confidence", 0.4);
    private double CookieIgnoredWeight => _configProvider.GetParameter(Name, "cookie_ignored_weight", 1.5);
    private double CookiePresentHumanConfidence => _configProvider.GetParameter(Name, "cookie_present_human_confidence", -0.15);
    private bool NoSetCookieNeutral => _configProvider.GetParameter(Name, "no_set_cookie_neutral", true);
    private int CacheExpirationMinutes => _configProvider.GetParameter(Name, "cache_expiration_minutes", 30);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var signature = sink.ReadHint(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return Task.FromResult(None());

        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return Task.FromResult(None());

        // Count cookies in the current request (semicolons + 1, or 0 if no Cookie header)
        var cookieHeader = context.Request.Headers["Cookie"].FirstOrDefault();
        var currentCookieCount = 0;
        if (!string.IsNullOrEmpty(cookieHeader))
            currentCookieCount = cookieHeader.Split(';').Length;

        var tracking = UpdateTracking(signature, currentCookieCount);

        var acceptanceRate = tracking.SetCookieCount > 0
            ? (double)tracking.MaxCookieCount / tracking.SetCookieCount
            : -1.0; // -1 means no Set-Cookie sent yet

        sink.Raise($"{SignalKeys.CookieAcceptanceRate}:{acceptanceRate.ToString("F3", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.CookieCount}:{currentCookieCount}", sessionId);

        // Not enough requests yet -- neutral / info
        if (tracking.RequestCount < MinRequestsForAnalysis)
        {
            return Task.FromResult(Single(DetectionContribution.Info(
                Name, Category, "Too few requests for cookie analysis")));
        }

        // Server hasn't sent any Set-Cookie yet -- can't judge
        if (tracking.SetCookieCount == 0 && NoSetCookieNeutral)
        {
            return Task.FromResult(Single(DetectionContribution.Info(
                Name, Category, "No Set-Cookie headers observed")));
        }

        // Bot signal: server has sent Set-Cookie but client never returns cookies
        if (tracking.SetCookieCount > 0 && tracking.MaxCookieCount == 0)
        {
            sink.Raise(SignalKeys.CookieIgnored, sessionId);

            _logger.LogDebug(
                "Cookie ignored: {Sig} setCookie={SetCookie} cookieCount=0 requests={Requests}",
                signature[..Math.Min(8, signature.Length)], tracking.SetCookieCount, tracking.RequestCount);

            return Task.FromResult(Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "CookieIgnored",
                ConfidenceDelta = CookieIgnoredConfidence,
                Weight = CookieIgnoredWeight,
                Reason = $"Cookies ignored: {tracking.SetCookieCount} Set-Cookie sent, 0 cookies returned over {tracking.RequestCount} requests",
                BotType = BotType.Scraper.ToString()
            }));
        }

        // Human signal: cookies present and growing over time
        if (tracking.RequestCount >= 5 && tracking.MaxCookieCount > 0 && tracking.CookieCountGrowing)
        {
            return Task.FromResult(Single(Human(
                confidence: -CookiePresentHumanConfidence, // Human() flips sign internally
                reason: $"Cookie accumulation pattern: {tracking.MaxCookieCount} cookies, growing over {tracking.RequestCount} requests")));
        }

        // Inconclusive
        return Task.FromResult(Single(DetectionContribution.Info(
            Name, Category, "Cookie behavior inconclusive")));
    }

    /// <summary>
    ///     Called by the middleware/response pipeline to record that a
    ///     Set-Cookie was sent. External contract inherited from the legacy
    ///     contributor -- must remain invocable from the response path.
    /// </summary>
    public void RecordSetCookie(string signature, int setCookieCount)
    {
        if (string.IsNullOrEmpty(signature) || setCookieCount <= 0) return;

        var key = $"{CachePrefix}{signature}";
        var state = _cache.Get<CookieTrackingState>(key) ?? new CookieTrackingState();
        state.SetCookieCount += setCookieCount;

        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(CacheExpirationMinutes)
        });
    }

    private CookieTrackingState UpdateTracking(string signature, int currentCookieCount)
    {
        var key = $"{CachePrefix}{signature}";
        var state = _cache.Get<CookieTrackingState>(key) ?? new CookieTrackingState();

        state.RequestCount++;

        var previousMax = state.MaxCookieCount;
        if (currentCookieCount > state.MaxCookieCount)
            state.MaxCookieCount = currentCookieCount;

        if (currentCookieCount > previousMax && previousMax > 0)
            state.GrowthCount++;
        state.CookieCountGrowing = state.GrowthCount >= 1;

        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(CacheExpirationMinutes)
        });

        return state;
    }

    /// <summary>Per-signature tracking state. Only counts, never cookie names or values.</summary>
    private sealed class CookieTrackingState
    {
        public int RequestCount { get; set; }
        public int SetCookieCount { get; set; }
        public int MaxCookieCount { get; set; }
        public int GrowthCount { get; set; }
        public bool CookieCountGrowing { get; set; }
    }
}
