using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Detects credential stuffing, brute force, phishing-sourced account takeover (ATO),
///     geographic velocity anomalies, and post-login behavioral drift.
///
///     Uses decay-aware behavioral baselines so that returning users after long absences
///     aren't unfairly flagged when their behavior naturally evolves (e.g., new device,
///     moved to a new country, changed browser). Baseline confidence decays exponentially
///     over time, requiring fresh observations to rebuild trust.
///
///     Runs in Wave 1 — needs signals from: UserAgent (ua.family), IP (ip.address hash),
///     Geo (geo.country_code), Behavioral (behavioral patterns), ResponseBehavior.
///
///     Cross-request state tracked per-signature via in-memory sliding window.
///     No credential content is ever inspected — zero-PII design. Login attempts are
///     counted by tracking POST requests to configurable login path patterns.
///
///     Drift score is a weighted composite of geo, fingerprint, timing, path, and velocity
///     drift dimensions, each normalized 0.0-1.0. The composite score determines whether
///     to flag a session as potential ATO.
///
///     Configuration loaded from: accounttakeover.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:AccountTakeoverContributor:*
/// </summary>
public class AccountTakeoverContributor : ConfiguredContributorBase
{
    private readonly ILogger<AccountTakeoverContributor> _logger;

    // Per-signature sliding window tracking
    private static readonly ConcurrentDictionary<string, LoginTracker> SignatureTrackers = new();

    // Periodic cleanup counter
    private static long _requestCounter;
    private const int CleanupInterval = 1000;

    // Cached path patterns — built once from YAML, avoids per-request GetStringListParam allocations
    private volatile CachedPathPatterns? _cachedPaths;

    // Static empty result to avoid allocation on clean path
    private static readonly Task<IReadOnlyList<DetectionContribution>> EmptyResult =
        Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

    public AccountTakeoverContributor(
        ILogger<AccountTakeoverContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "AccountTakeover";
    public override int Priority => Manifest?.Priority ?? 25;

    // Wave 1 — needs signals from earlier detectors
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.AnyOf(
            Triggers.WhenSignalExists(SignalKeys.UserAgentFamily),
            Triggers.WhenSignalExists(SignalKeys.WaveformSignature)
        )
    ];

    // Config-driven parameters
    private int FailedLoginThreshold => GetParam("failed_login_threshold", 5);
    private int FailedLoginWindowMinutes => GetParam("failed_login_window_minutes", 5);
    private int BruteForceThreshold => GetParam("brute_force_threshold", 10);
    private int BruteForceWindowMinutes => GetParam("brute_force_window_minutes", 5);
    private int RapidChangeThresholdSeconds => GetParam("rapid_change_threshold_seconds", 60);
    private int WindowSizeMinutes => GetParam("window_size_minutes", 30);
    private int MaxTrackedSignatures => GetParam("max_tracked_signatures", 10000);

    // Confidence per pattern
    private double StuffingConfidence => GetParam("stuffing_confidence", 0.90);
    private double BruteForceConfidenceValue => GetParam("brute_force_confidence", 0.90);
    private double DirectPostConfidenceValue => GetParam("direct_post_confidence", 0.60);
    private double RapidChangeConfidenceValue => GetParam("rapid_change_confidence", 0.85);
    private double GeoVelocityConfidenceValue => GetParam("geo_velocity_confidence", 0.88);

    // Drift score weights (must sum to ~1.0)
    private double DriftWeightGeo => GetParam("drift_weight_geo", 0.30);
    private double DriftWeightFingerprint => GetParam("drift_weight_fingerprint", 0.25);
    private double DriftWeightTiming => GetParam("drift_weight_timing", 0.15);
    private double DriftWeightPath => GetParam("drift_weight_path", 0.20);
    private double DriftWeightVelocity => GetParam("drift_weight_velocity", 0.10);

    // Decay: half-life in days — after this many days of absence,
    // the baseline confidence halves, reducing false positives for returning users
    private double BaselineHalfLifeDays => GetParam("baseline_half_life_days", 14.0);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var signature = state.GetSignal<string>(SignalKeys.WaveformSignature);
            if (string.IsNullOrEmpty(signature))
                return EmptyResult;

            var path = state.HttpContext.Request.Path.Value ?? "/";
            var method = state.HttpContext.Request.Method;
            var now = DateTimeOffset.UtcNow;

            // Periodic cleanup of stale trackers — only every N requests
            if (Interlocked.Increment(ref _requestCounter) % CleanupInterval == 0)
                CleanupStaleTrackers(now);

            // Get cached path patterns (built once from YAML config)
            var pathPatterns = EnsureCachedPaths();

            // Use HttpMethods helpers — zero-allocation string comparison
            var isPost = HttpMethods.IsPost(method);
            var isGet = HttpMethods.IsGet(method);

            // Fast path: if not a login/sensitive path and not POST, only compute drift
            var pathSpan = path.AsSpan();
            var isLoginPath = MatchesAnyPath(pathSpan, pathPatterns.LoginPaths);
            var isSensitivePath = MatchesAnyPath(pathSpan, pathPatterns.SensitivePaths);

            // If this request doesn't touch login/sensitive paths, only compute drift if tracker exists
            if (!isLoginPath && !isSensitivePath)
            {
                // Only compute drift for already-tracked signatures (don't create tracker for non-login traffic)
                if (SignatureTrackers.TryGetValue(signature, out var existingTracker))
                {
                    var driftScore = ComputeDriftScore(state, existingTracker, now);
                    if (driftScore > 0.01)
                    {
                        state.WriteSignal(SignalKeys.AtoDriftScore, driftScore);
                        existingTracker.LastSeen = now;

                        // Only create contributions for high drift
                        if (driftScore > 0.6 && state.GetSignal<bool>(SignalKeys.GeoChangeDriftDetected))
                        {
                            return Task.FromResult(BuildGeoVelocityContribution(state, driftScore));
                        }
                    }
                    else
                    {
                        existingTracker.LastSeen = now;
                    }
                }
                return EmptyResult;
            }

            // === Login/sensitive path — full analysis ===
            var tracker = SignatureTrackers.GetOrAdd(signature, static _ => new LoginTracker());

            // Prune the tracker's expired entries
            var windowCutoff = now.AddMinutes(-WindowSizeMinutes);
            tracker.PruneExpired(windowCutoff);

            var contributions = new List<DetectionContribution>(4); // Pre-size for typical max

            // Track login page views (GET) for "direct POST" detection
            if (isLoginPath && isGet)
            {
                tracker.LastLoginPageView = now;
            }

            // Track POST to login endpoints (login attempts)
            if (isLoginPath && isPost)
            {
                tracker.RecordLoginAttempt(now);

                // Check: direct POST without prior GET (skipped form page)
                if (tracker.LastLoginPageView == null ||
                    (now - tracker.LastLoginPageView.Value).TotalMinutes > 5)
                {
                    tracker.DirectPostCount++;
                }

                // Check for auth failures from ResponseBehavior signals
                var authFailures = state.GetSignal<int>(SignalKeys.ResponseAuthFailures);
                if (authFailures > 0)
                    tracker.FailedLoginCount += authFailures;

                // Credential stuffing: many failures in window
                if (tracker.FailedLoginCount >= FailedLoginThreshold)
                {
                    state.WriteSignal(SignalKeys.AtoDetected, true);
                    state.WriteSignal(SignalKeys.AtoCredentialStuffing, true);
                    state.WriteSignal(SignalKeys.AtoLoginFailedCount, tracker.FailedLoginCount);

                    contributions.Add(StrongBotContribution(
                        "AccountTakeover",
                        $"Credential stuffing: {tracker.FailedLoginCount} failed logins in {FailedLoginWindowMinutes}min window",
                        botType: BotType.MaliciousBot.ToString(),
                        botName: "CredentialStuffer") with
                    {
                        ConfidenceDelta = StuffingConfidence,
                    });
                }

                // Brute force: many total attempts
                if (tracker.TotalLoginAttempts >= BruteForceThreshold)
                {
                    state.WriteSignal(SignalKeys.AtoDetected, true);
                    state.WriteSignal(SignalKeys.AtoBruteForce, true);

                    contributions.Add(BotContribution(
                        "AccountTakeover",
                        $"Brute force: {tracker.TotalLoginAttempts} login attempts in {BruteForceWindowMinutes}min window",
                        confidenceOverride: BruteForceConfidenceValue,
                        weightMultiplier: 2.0,
                        botType: BotType.MaliciousBot.ToString()));
                }

                // Direct POST (no form render)
                if (tracker.DirectPostCount >= 2)
                {
                    state.WriteSignal(SignalKeys.AtoDirectPost, true);

                    contributions.Add(BotContribution(
                        "AccountTakeover",
                        $"Direct POST to login without prior page load ({tracker.DirectPostCount} times)",
                        confidenceOverride: DirectPostConfidenceValue,
                        weightMultiplier: 1.2));
                }
            }

            // Track sensitive path access after login
            if (isSensitivePath)
            {
                var timeSinceLogin = tracker.LastSuccessfulLogin.HasValue
                    ? (now - tracker.LastSuccessfulLogin.Value).TotalSeconds
                    : double.MaxValue;

                if (timeSinceLogin < RapidChangeThresholdSeconds && timeSinceLogin > 0)
                {
                    state.WriteSignal(SignalKeys.AtoDetected, true);
                    state.WriteSignal(SignalKeys.AtoRapidCredentialChange, true);

                    contributions.Add(BotContribution(
                        "AccountTakeover",
                        $"Rapid sensitive action: login -> {path} in {timeSinceLogin:F0}s (threshold: {RapidChangeThresholdSeconds}s)",
                        confidenceOverride: RapidChangeConfidenceValue,
                        weightMultiplier: 1.8,
                        botType: BotType.MaliciousBot.ToString()));
                }
            }

            // Track successful logins (200/302 response to login POST)
            if (isLoginPath && isPost)
            {
                var authFailures = state.GetSignal<int>(SignalKeys.ResponseAuthFailures);
                if (authFailures == 0)
                {
                    tracker.LastSuccessfulLogin = now;
                    tracker.LastLoginCountryCode = state.GetSignal<string>(SignalKeys.GeoCountryCode);
                }
            }

            // Compute drift score
            var drift = ComputeDriftScore(state, tracker, now);
            if (drift > 0.01)
            {
                state.WriteSignal(SignalKeys.AtoDriftScore, drift);

                var geoChanged = state.GetSignal<bool>(SignalKeys.GeoChangeDriftDetected);
                if (geoChanged)
                {
                    state.WriteSignal(SignalKeys.AtoDriftGeo, true);

                    if (drift > 0.6)
                    {
                        state.WriteSignal(SignalKeys.AtoDetected, true);
                        state.WriteSignal(SignalKeys.AtoGeoVelocity, true);

                        contributions.Add(BotContribution(
                            "AccountTakeover",
                            $"Geographic velocity anomaly: country changed with drift score {drift:F2}",
                            confidenceOverride: GeoVelocityConfidenceValue * drift,
                            weightMultiplier: 1.5,
                            botType: BotType.MaliciousBot.ToString()));
                    }
                }
            }

            tracker.LastSeen = now;

            return contributions.Count > 0
                ? Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions)
                : EmptyResult;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in AccountTakeoverContributor");
            return EmptyResult;
        }
    }

    /// <summary>
    ///     Build a geo velocity contribution — extracted to avoid allocation in common path.
    /// </summary>
    private IReadOnlyList<DetectionContribution> BuildGeoVelocityContribution(
        BlackboardState state, double driftScore)
    {
        state.WriteSignal(SignalKeys.AtoDriftGeo, true);
        state.WriteSignal(SignalKeys.AtoDetected, true);
        state.WriteSignal(SignalKeys.AtoGeoVelocity, true);

        return
        [
            BotContribution(
                "AccountTakeover",
                $"Geographic velocity anomaly: country changed with drift score {driftScore:F2}",
                confidenceOverride: GeoVelocityConfidenceValue * driftScore,
                weightMultiplier: 1.5,
                botType: BotType.MaliciousBot.ToString())
        ];
    }

    /// <summary>
    ///     Compute a composite behavioral drift score (0.0-1.0).
    ///     Each dimension is individually normalized and weighted.
    ///     The baseline decays exponentially based on time since last seen,
    ///     so returning users after long absences get a lower drift penalty.
    /// </summary>
    private double ComputeDriftScore(BlackboardState state, LoginTracker tracker, DateTimeOffset now)
    {
        // Calculate decay factor: how much we trust the baseline
        var daysSinceLastSeen = tracker.LastSeen.HasValue
            ? (now - tracker.LastSeen.Value).TotalDays
            : 0.0;

        // Exponential decay: trust = 2^(-days / halfLife)
        var baselineTrust = Math.Pow(2.0, -daysSinceLastSeen / BaselineHalfLifeDays);

        // If baseline trust is very low, drift is unreliable
        if (baselineTrust < 0.1)
            return 0.0;

        var geoDrift = 0.0;
        var fingerprintDrift = 0.0;
        var timingDrift = 0.0;
        var pathDrift = 0.0;
        var velocityDrift = 0.0;

        // Geo drift: did country change?
        if (state.GetSignal<bool>(SignalKeys.GeoChangeDriftDetected))
            geoDrift = 1.0;

        state.WriteSignal(SignalKeys.AtoDriftGeo, geoDrift > 0);

        // Fingerprint drift
        var correlationAnomalies = state.GetSignal<int>(SignalKeys.CorrelationAnomalyCount);
        if (correlationAnomalies > 0)
            fingerprintDrift = Math.Min(correlationAnomalies / 3.0, 1.0);

        state.WriteSignal(SignalKeys.AtoDriftFingerprint, fingerprintDrift > 0.3);

        // Timing drift
        var timingRegularity = state.GetSignal<double>(SignalKeys.WaveformTimingRegularity);
        if (timingRegularity > 0.8)
            timingDrift = timingRegularity;

        state.WriteSignal(SignalKeys.AtoDriftTiming, timingDrift);

        // Path drift
        var pathDiversity = state.GetSignal<double>(SignalKeys.WaveformPathDiversity);
        if (tracker.BaselinePathDiversity > 0 && pathDiversity > 0)
        {
            pathDrift = Math.Abs(pathDiversity - tracker.BaselinePathDiversity);
            pathDrift = Math.Min(pathDrift * 2.0, 1.0);
        }
        if (pathDiversity > 0)
            tracker.BaselinePathDiversity = tracker.BaselinePathDiversity > 0
                ? tracker.BaselinePathDiversity * 0.9 + pathDiversity * 0.1
                : pathDiversity;

        state.WriteSignal(SignalKeys.AtoDriftPath, pathDrift);

        // Velocity drift
        if (state.GetSignal<bool>(SignalKeys.WaveformBurstDetected))
            velocityDrift = 0.8;

        state.WriteSignal(SignalKeys.AtoDriftVelocity, velocityDrift);

        // Weighted composite, attenuated by baseline trust (decay)
        var rawDrift = geoDrift * DriftWeightGeo
                       + fingerprintDrift * DriftWeightFingerprint
                       + timingDrift * DriftWeightTiming
                       + pathDrift * DriftWeightPath
                       + velocityDrift * DriftWeightVelocity;

        return Math.Min(rawDrift * baselineTrust, 1.0);
    }

    /// <summary>
    ///     Path matching using pre-built pattern entries. Uses OrdinalIgnoreCase
    ///     to avoid allocating lowered string copies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesAnyPath(ReadOnlySpan<char> path, PathEntry[] patterns)
    {
        foreach (ref readonly var entry in patterns.AsSpan())
        {
            if (entry.IsPrefix)
            {
                if (path.StartsWith(entry.Pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                if (path.Equals(entry.Pattern, StringComparison.OrdinalIgnoreCase) ||
                    (path.Length > entry.Pattern.Length &&
                     path.StartsWith(entry.Pattern, StringComparison.OrdinalIgnoreCase) &&
                     path[entry.Pattern.Length] == '/'))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    ///     Build and cache path patterns from YAML config. Called once.
    /// </summary>
    private CachedPathPatterns EnsureCachedPaths()
    {
        var existing = _cachedPaths;
        if (existing != null)
            return existing;

        var result = new CachedPathPatterns(
            BuildEntries("login_paths"),
            BuildEntries("sensitive_paths"));
        _cachedPaths = result;
        return result;
    }

    private PathEntry[] BuildEntries(string paramName)
    {
        var patterns = GetStringListParam(paramName);
        var entries = new PathEntry[patterns.Count];
        for (var i = 0; i < patterns.Count; i++)
        {
            var p = patterns[i];
            var isPrefix = p.EndsWith('*');
            entries[i] = new PathEntry(isPrefix ? p[..^1] : p, isPrefix);
        }
        return entries;
    }

    private void CleanupStaleTrackers(DateTimeOffset now)
    {
        var expiry = now.AddMinutes(-WindowSizeMinutes * 2);

        // Single pass: remove stale entries without intermediate list allocation
        foreach (var kvp in SignatureTrackers)
        {
            if (kvp.Value.LastSeen.HasValue && kvp.Value.LastSeen.Value < expiry)
                SignatureTrackers.TryRemove(kvp.Key, out _);
        }

        // Hard cap on tracked signatures
        var count = SignatureTrackers.Count;
        if (count > MaxTrackedSignatures)
        {
            // Evict oldest entries — use a simple approach: remove entries that are oldest
            var toEvict = count - MaxTrackedSignatures + 100;
            var oldest = DateTimeOffset.MaxValue;
            string? oldestKey = null;

            for (var i = 0; i < toEvict; i++)
            {
                oldest = DateTimeOffset.MaxValue;
                oldestKey = null;

                foreach (var kvp in SignatureTrackers)
                {
                    var seen = kvp.Value.LastSeen ?? DateTimeOffset.MinValue;
                    if (seen < oldest)
                    {
                        oldest = seen;
                        oldestKey = kvp.Key;
                    }
                }

                if (oldestKey != null)
                    SignatureTrackers.TryRemove(oldestKey, out _);
            }
        }
    }

    // ===== Internal types =====

    private readonly record struct PathEntry(string Pattern, bool IsPrefix);

    private sealed class CachedPathPatterns(PathEntry[] loginPaths, PathEntry[] sensitivePaths)
    {
        public PathEntry[] LoginPaths { get; } = loginPaths;
        public PathEntry[] SensitivePaths { get; } = sensitivePaths;
    }

    /// <summary>
    ///     Per-signature login attempt tracker with sliding window.
    ///     Thread-safe via lock on mutable state.
    /// </summary>
    private sealed class LoginTracker
    {
        private readonly object _lock = new();
        private readonly List<DateTimeOffset> _loginAttempts = new(8); // Pre-size for typical

        public int FailedLoginCount;
        public int DirectPostCount;

        public DateTimeOffset? LastLoginPageView { get; set; }
        public DateTimeOffset? LastSuccessfulLogin { get; set; }
        public DateTimeOffset? LastSeen { get; set; }
        public string? LastLoginCountryCode { get; set; }
        public double BaselinePathDiversity { get; set; }

        public int TotalLoginAttempts
        {
            get
            {
                lock (_lock) return _loginAttempts.Count;
            }
        }

        public void RecordLoginAttempt(DateTimeOffset time)
        {
            lock (_lock)
            {
                _loginAttempts.Add(time);
            }
        }

        public void PruneExpired(DateTimeOffset cutoff)
        {
            lock (_lock)
            {
                _loginAttempts.RemoveAll(t => t < cutoff);

                if (_loginAttempts.Count == 0)
                {
                    FailedLoginCount = 0;
                    DirectPostCount = 0;
                }
            }
        }
    }
}
