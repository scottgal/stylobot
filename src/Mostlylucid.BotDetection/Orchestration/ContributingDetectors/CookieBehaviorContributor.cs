using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Detects bots that ignore Set-Cookie headers.
///     HTTP libraries (Python requests, Go net/http, Node axios, curl) typically
///     discard Set-Cookie headers entirely. Real browsers always handle cookies.
///
///     Tracks per-signature:
///     - How many Set-Cookie headers the server has sent (from previous responses)
///     - How many cookies the client sends (from Cookie header)
///     - Total request count
///
///     Zero-PII: only counts are tracked, never cookie names or values.
/// </summary>
public class CookieBehaviorContributor : ConfiguredContributorBase
{
    private readonly ILogger<CookieBehaviorContributor> _logger;
    private readonly IMemoryCache _cache;

    private const string CachePrefix = "cookiebehavior:";

    // Configurable via YAML
    private int MinRequestsForAnalysis => GetParam("min_requests_for_analysis", 3);
    private double CookieIgnoredConfidence => GetParam("cookie_ignored_confidence", 0.4);
    private double CookieIgnoredWeight => GetParam("cookie_ignored_weight", 1.5);
    private double CookiePresentHumanConfidence => GetParam("cookie_present_human_confidence", -0.15);
    private bool NoSetCookieNeutral => GetParam("no_set_cookie_neutral", true);
    private int CacheExpirationMinutes => GetParam("cache_expiration_minutes", 30);

    public CookieBehaviorContributor(
        ILogger<CookieBehaviorContributor> logger,
        IDetectorConfigProvider configProvider,
        IMemoryCache cache) : base(configProvider)
    {
        _logger = logger;
        _cache = cache;
    }

    public override string Name => "CookieBehavior";
    public override int Priority => 20; // After headers, before behavioral analysis

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        new SignalExistsTrigger(SignalKeys.PrimarySignature)
    ];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = new Dictionary<string, object>();

        var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        // Get the HTTP context from the blackboard to inspect headers
        var httpContext = state.HttpContext;
        if (httpContext == null)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

        // Count cookies in the current request (count semicolons + 1, or 0 if no Cookie header)
        var cookieHeader = httpContext.Request.Headers["Cookie"].FirstOrDefault();
        var currentCookieCount = 0;
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            currentCookieCount = cookieHeader.Split(';').Length;
        }

        // Update per-signature tracking state
        var tracking = UpdateTracking(signature, currentCookieCount);

        // Write signals to blackboard
        var acceptanceRate = tracking.SetCookieCount > 0
            ? (double)tracking.MaxCookieCount / tracking.SetCookieCount
            : -1.0; // -1 means no Set-Cookie sent yet

        signals[SignalKeys.CookieAcceptanceRate] = acceptanceRate;
        signals[SignalKeys.CookieCount] = currentCookieCount;
        signals[SignalKeys.CookieIgnored] = false; // default, may be overridden below

        foreach (var (key, value) in signals)
            state.WriteSignal(key, value);

        // Not enough requests yet - neutral
        if (tracking.RequestCount < MinRequestsForAnalysis)
        {
            contributions.Add(NeutralContribution("CookieBehavior", "Too few requests for cookie analysis"));
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        // Server hasn't sent any Set-Cookie yet - can't judge
        if (tracking.SetCookieCount == 0 && NoSetCookieNeutral)
        {
            contributions.Add(NeutralContribution("CookieBehavior", "No Set-Cookie headers observed"));
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        // Bot signal: server has sent Set-Cookie but client never returns cookies
        if (tracking.SetCookieCount > 0 && tracking.MaxCookieCount == 0)
        {
            state.WriteSignal(SignalKeys.CookieIgnored, true);

            contributions.Add(BotContribution(
                "CookieIgnored",
                $"Cookies ignored: {tracking.SetCookieCount} Set-Cookie sent, 0 cookies returned over {tracking.RequestCount} requests",
                confidenceOverride: CookieIgnoredConfidence,
                weightMultiplier: CookieIgnoredWeight,
                botType: BotType.Scraper.ToString()));

            _logger.LogDebug("Cookie ignored: {Sig} setCookie={SetCookie} cookieCount=0 requests={Requests}",
                signature[..Math.Min(8, signature.Length)], tracking.SetCookieCount, tracking.RequestCount);

            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        // Human signal: cookies present and growing over time (accumulation pattern)
        if (tracking.RequestCount >= 5 && tracking.MaxCookieCount > 0 && tracking.CookieCountGrowing)
        {
            contributions.Add(HumanContribution(
                "CookieAccepted",
                $"Cookie accumulation pattern: {tracking.MaxCookieCount} cookies, growing over {tracking.RequestCount} requests"));

            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        // Neutral - cookies present but not enough data to be conclusive
        contributions.Add(NeutralContribution("CookieBehavior", "Cookie behavior inconclusive"));
        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private CookieTrackingState UpdateTracking(string signature, int currentCookieCount)
    {
        var key = $"{CachePrefix}{signature}";
        var state = _cache.Get<CookieTrackingState>(key) ?? new CookieTrackingState();

        state.RequestCount++;

        // Track cookie count trajectory
        var previousMax = state.MaxCookieCount;
        if (currentCookieCount > state.MaxCookieCount)
            state.MaxCookieCount = currentCookieCount;

        // Track if cookies are growing (at least 2 increases seen)
        if (currentCookieCount > previousMax && previousMax > 0)
            state.GrowthCount++;

        state.CookieCountGrowing = state.GrowthCount >= 1;

        // Check if the current response has Set-Cookie (from previous response cycle)
        // We increment SetCookieCount via the response callback registration below.
        // For the contributor, we rely on the cached state which is updated after each response.

        _cache.Set(key, state, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(CacheExpirationMinutes)
        });

        return state;
    }

    /// <summary>
    ///     Called by the middleware/response pipeline to record that a Set-Cookie was sent.
    ///     This must be invoked from the response path to track server-side cookie issuance.
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

    /// <summary>
    ///     Per-signature tracking state. Only counts - never cookie names or values (zero-PII).
    /// </summary>
    private class CookieTrackingState
    {
        public int RequestCount { get; set; }
        public int SetCookieCount { get; set; }
        public int MaxCookieCount { get; set; }
        public int GrowthCount { get; set; }
        public bool CookieCountGrowing { get; set; }
    }
}