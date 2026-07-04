using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that detects credential stuffing,
///     brute force, phishing-sourced account takeover (ATO), geographic
///     velocity anomalies, and post-login behavioural drift.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>AccountTakeoverContributor</c>. Priority 25.
///     </para>
///     <para>
///         Per-signature <see cref="LoginTracker"/> cross-request state
///         stays on the atom instance (singleton). No credential CONTENT
///         is ever inspected -- login attempts are counted by tracking
///         POST requests to configurable login-path patterns; zero-PII
///         design preserved verbatim.
///     </para>
///     <para>
///         Baseline confidence decays exponentially so returning users
///         after long absences aren't unfairly flagged when their
///         behaviour naturally evolves.
///     </para>
/// </remarks>
public sealed class AccountTakeoverAtom : DetectorAtomBase
{
    private readonly ConcurrentDictionary<string, LoginTracker> _trackers = new();
    private long _requestCounter;
    private const int CleanupInterval = 1000;
    private volatile CachedPathPatterns? _cachedPaths;

    private readonly ILogger<AccountTakeoverAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AccountTakeoverAtom(
        ILogger<AccountTakeoverAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "AccountTakeover", category: "AccountTakeover")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 25;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.PrimarySignature };

    private int FailedLoginThreshold => _configProvider.GetParameter(Name, "failed_login_threshold", 5);
    private int FailedLoginWindowMinutes => _configProvider.GetParameter(Name, "failed_login_window_minutes", 5);
    private int BruteForceThreshold => _configProvider.GetParameter(Name, "brute_force_threshold", 10);
    private int BruteForceWindowMinutes => _configProvider.GetParameter(Name, "brute_force_window_minutes", 5);
    private int RapidChangeThresholdSeconds => _configProvider.GetParameter(Name, "rapid_change_threshold_seconds", 60);
    private int WindowSizeMinutes => _configProvider.GetParameter(Name, "window_size_minutes", 30);
    private int MaxTrackedSignatures => _configProvider.GetParameter(Name, "max_tracked_signatures", 10000);

    private double StuffingConfidence => _configProvider.GetParameter(Name, "stuffing_confidence", 0.90);
    private double BruteForceConfidenceValue => _configProvider.GetParameter(Name, "brute_force_confidence", 0.90);
    private double DirectPostConfidenceValue => _configProvider.GetParameter(Name, "direct_post_confidence", 0.60);
    private double RapidChangeConfidenceValue => _configProvider.GetParameter(Name, "rapid_change_confidence", 0.85);
    private double GeoVelocityConfidenceValue => _configProvider.GetParameter(Name, "geo_velocity_confidence", 0.88);

    private double DriftWeightGeo => _configProvider.GetParameter(Name, "drift_weight_geo", 0.30);
    private double DriftWeightFingerprint => _configProvider.GetParameter(Name, "drift_weight_fingerprint", 0.25);
    private double DriftWeightTiming => _configProvider.GetParameter(Name, "drift_weight_timing", 0.15);
    private double DriftWeightPath => _configProvider.GetParameter(Name, "drift_weight_path", 0.20);
    private double DriftWeightVelocity => _configProvider.GetParameter(Name, "drift_weight_velocity", 0.10);

    private double BaselineHalfLifeDays => _configProvider.GetParameter(Name, "baseline_half_life_days", 14.0);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        try
        {
            var signature = sink.ReadHint(SignalKeys.PrimarySignature);
            if (string.IsNullOrEmpty(signature)) return Task.FromResult(None());

            var path = context.Request.Path.Value ?? "/";
            var method = context.Request.Method;
            var now = DateTimeOffset.UtcNow;

            if (Interlocked.Increment(ref _requestCounter) % CleanupInterval == 0)
                CleanupStaleTrackers(now);

            var pathPatterns = EnsureCachedPaths();
            var isPost = HttpMethods.IsPost(method);
            var isGet = HttpMethods.IsGet(method);
            var pathSpan = path.AsSpan();
            var isLoginPath = MatchesAnyPath(pathSpan, pathPatterns.LoginPaths);
            var isSensitivePath = MatchesAnyPath(pathSpan, pathPatterns.SensitivePaths);

            if (!isLoginPath && !isSensitivePath)
            {
                if (_trackers.TryGetValue(signature, out var existingTracker))
                {
                    var driftScore = ComputeDriftScore(sink, sessionId, existingTracker, now);
                    if (driftScore > 0.01)
                    {
                        sink.Raise($"{SignalKeys.AtoDriftScore}:{driftScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);
                        existingTracker.LastSeen = now;

                        if (driftScore > 0.6 && sink.ReadBoolHint(SignalKeys.GeoChangeDriftDetected))
                            return Task.FromResult(BuildGeoVelocityContribution(sink, sessionId, driftScore));
                    }
                    else
                    {
                        existingTracker.LastSeen = now;
                    }
                }
                return Task.FromResult(None());
            }

            var tracker = _trackers.GetOrAdd(signature, static _ => new LoginTracker());
            var windowCutoff = now.AddMinutes(-WindowSizeMinutes);
            tracker.PruneExpired(windowCutoff);

            var contributions = new List<DetectionContribution>(4);

            if (isLoginPath && isGet) tracker.LastLoginPageView = now;

            if (isLoginPath && isPost)
            {
                tracker.RecordLoginAttempt(now);

                if (tracker.LastLoginPageView is null
                    || (now - tracker.LastLoginPageView.Value).TotalMinutes > 5)
                    tracker.DirectPostCount++;

                var authFailures = sink.ReadIntHint(SignalKeys.ResponseAuthFailures);
                if (authFailures > 0) tracker.FailedLoginCount += authFailures;

                if (tracker.FailedLoginCount >= FailedLoginThreshold)
                {
                    sink.Raise($"{SignalKeys.AtoDetected}:true", sessionId);
                    sink.Raise($"{SignalKeys.AtoCredentialStuffing}:true", sessionId);
                    sink.Raise($"{SignalKeys.AtoLoginFailedCount}:{tracker.FailedLoginCount}", sessionId);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = StuffingConfidence,
                        Weight = 2.0,
                        Reason = $"Credential stuffing: {tracker.FailedLoginCount} failed logins in {FailedLoginWindowMinutes}min window",
                        BotType = BotType.MaliciousBot.ToString(),
                        BotName = "CredentialStuffer"
                    });
                }

                if (tracker.TotalLoginAttempts >= BruteForceThreshold)
                {
                    sink.Raise($"{SignalKeys.AtoDetected}:true", sessionId);
                    sink.Raise($"{SignalKeys.AtoBruteForce}:true", sessionId);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = BruteForceConfidenceValue,
                        Weight = 2.0,
                        Reason = $"Brute force: {tracker.TotalLoginAttempts} login attempts in {BruteForceWindowMinutes}min window",
                        BotType = BotType.MaliciousBot.ToString()
                    });
                }

                if (tracker.DirectPostCount >= 2)
                {
                    sink.Raise($"{SignalKeys.AtoDirectPost}:true", sessionId);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = DirectPostConfidenceValue,
                        Weight = 1.2,
                        Reason = $"Direct POST to login without prior page load ({tracker.DirectPostCount} times)",
                        BotType = BotType.Unknown.ToString()
                    });
                }
            }

            if (isSensitivePath)
            {
                var timeSinceLogin = tracker.LastSuccessfulLogin.HasValue
                    ? (now - tracker.LastSuccessfulLogin.Value).TotalSeconds
                    : double.MaxValue;

                if (timeSinceLogin < RapidChangeThresholdSeconds && timeSinceLogin > 0)
                {
                    sink.Raise($"{SignalKeys.AtoDetected}:true", sessionId);
                    sink.Raise($"{SignalKeys.AtoRapidCredentialChange}:true", sessionId);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = RapidChangeConfidenceValue,
                        Weight = 1.8,
                        Reason = $"Rapid sensitive action: login -> {path} in {timeSinceLogin:F0}s (threshold: {RapidChangeThresholdSeconds}s)",
                        BotType = BotType.MaliciousBot.ToString()
                    });
                }
            }

            if (isLoginPath && isPost)
            {
                var authFailures = sink.ReadIntHint(SignalKeys.ResponseAuthFailures);
                if (authFailures == 0)
                {
                    tracker.LastSuccessfulLogin = now;
                    tracker.LastLoginCountryCode = sink.ReadHint(SignalKeys.GeoCountryCode);
                }
            }

            var drift = ComputeDriftScore(sink, sessionId, tracker, now);
            if (drift > 0.01)
            {
                sink.Raise($"{SignalKeys.AtoDriftScore}:{drift.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);
                var geoChanged = sink.ReadBoolHint(SignalKeys.GeoChangeDriftDetected);
                if (geoChanged)
                {
                    sink.Raise($"{SignalKeys.AtoDriftGeo}:true", sessionId);
                    if (drift > 0.6)
                    {
                        sink.Raise($"{SignalKeys.AtoDetected}:true", sessionId);
                        sink.Raise($"{SignalKeys.AtoGeoVelocity}:true", sessionId);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = GeoVelocityConfidenceValue * drift,
                            Weight = 1.5,
                            Reason = $"Geographic velocity anomaly: country changed with drift score {drift:F2}",
                            BotType = BotType.MaliciousBot.ToString()
                        });
                    }
                }
            }

            tracker.LastSeen = now;

            return contributions.Count > 0
                ? Task.FromResult((IReadOnlyList<DetectionContribution>)contributions)
                : Task.FromResult(None());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in AccountTakeoverAtom");
            return Task.FromResult(None());
        }
    }

    private IReadOnlyList<DetectionContribution> BuildGeoVelocityContribution(
        SignalSink sink, string sessionId, double driftScore)
    {
        sink.Raise($"{SignalKeys.AtoDriftGeo}:true", sessionId);
        sink.Raise($"{SignalKeys.AtoDetected}:true", sessionId);
        sink.Raise($"{SignalKeys.AtoGeoVelocity}:true", sessionId);
        return Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = GeoVelocityConfidenceValue * driftScore,
            Weight = 1.5,
            Reason = $"Geographic velocity anomaly: country changed with drift score {driftScore:F2}",
            BotType = BotType.MaliciousBot.ToString()
        });
    }

    private double ComputeDriftScore(SignalSink sink, string sessionId, LoginTracker tracker, DateTimeOffset now)
    {
        var daysSinceLastSeen = tracker.LastSeen.HasValue
            ? (now - tracker.LastSeen.Value).TotalDays
            : 0.0;
        var baselineTrust = Math.Pow(2.0, -daysSinceLastSeen / BaselineHalfLifeDays);
        if (baselineTrust < 0.1) return 0.0;

        var geoDrift = 0.0;
        var fingerprintDrift = 0.0;
        var timingDrift = 0.0;
        var pathDrift = 0.0;
        var velocityDrift = 0.0;

        if (sink.ReadBoolHint(SignalKeys.GeoChangeDriftDetected)) geoDrift = 1.0;
        sink.Raise($"{SignalKeys.AtoDriftGeo}:{(geoDrift > 0 ? "true" : "false")}", sessionId);

        var correlationAnomalies = sink.ReadIntHint(SignalKeys.CorrelationAnomalyCount);
        if (correlationAnomalies > 0)
            fingerprintDrift = Math.Min(correlationAnomalies / 3.0, 1.0);
        sink.Raise($"{SignalKeys.AtoDriftFingerprint}:{(fingerprintDrift > 0.3 ? "true" : "false")}", sessionId);

        var timingRegularity = sink.ReadDoubleHint(SignalKeys.WaveformTimingRegularity);
        if (timingRegularity > 0.8) timingDrift = timingRegularity;
        sink.Raise($"{SignalKeys.AtoDriftTiming}:{timingDrift.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);

        var pathDiversity = sink.ReadDoubleHint(SignalKeys.WaveformPathDiversity);
        if (tracker.BaselinePathDiversity > 0 && pathDiversity > 0)
        {
            pathDrift = Math.Abs(pathDiversity - tracker.BaselinePathDiversity);
            pathDrift = Math.Min(pathDrift * 2.0, 1.0);
        }
        if (pathDiversity > 0)
            tracker.BaselinePathDiversity = tracker.BaselinePathDiversity > 0
                ? tracker.BaselinePathDiversity * 0.9 + pathDiversity * 0.1
                : pathDiversity;
        sink.Raise($"{SignalKeys.AtoDriftPath}:{pathDrift.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);

        if (sink.ReadBoolHint(SignalKeys.WaveformBurstDetected)) velocityDrift = 0.8;
        sink.Raise($"{SignalKeys.AtoDriftVelocity}:{velocityDrift.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);

        var rawDrift = geoDrift * DriftWeightGeo
                       + fingerprintDrift * DriftWeightFingerprint
                       + timingDrift * DriftWeightTiming
                       + pathDrift * DriftWeightPath
                       + velocityDrift * DriftWeightVelocity;
        return Math.Min(rawDrift * baselineTrust, 1.0);
    }

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
                if (path.Equals(entry.Pattern, StringComparison.OrdinalIgnoreCase)
                    || (path.Length > entry.Pattern.Length
                        && path.StartsWith(entry.Pattern, StringComparison.OrdinalIgnoreCase)
                        && path[entry.Pattern.Length] == '/'))
                    return true;
            }
        }
        return false;
    }

    private CachedPathPatterns EnsureCachedPaths()
    {
        var existing = _cachedPaths;
        if (existing is not null) return existing;
        var result = new CachedPathPatterns(BuildEntries("login_paths"), BuildEntries("sensitive_paths"));
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

    private IReadOnlyList<string> GetStringListParam(string paramName)
    {
        var parameters = _configProvider.GetDefaults(Name).Parameters;
        if (parameters.TryGetValue(paramName, out var value))
        {
            if (value is IEnumerable<string> strings) return strings.ToArray();
            if (value is IEnumerable<object> enumerable)
                return enumerable.Select(x => x?.ToString() ?? string.Empty).ToArray();
        }
        return Array.Empty<string>();
    }

    private void CleanupStaleTrackers(DateTimeOffset now)
    {
        var expiry = now.AddMinutes(-WindowSizeMinutes * 2);
        foreach (var kvp in _trackers)
        {
            if (kvp.Value.LastSeen.HasValue && kvp.Value.LastSeen.Value < expiry)
                _trackers.TryRemove(kvp.Key, out _);
        }

        var count = _trackers.Count;
        if (count > MaxTrackedSignatures)
        {
            var toEvictKeys = _trackers
                .OrderBy(kvp => kvp.Value.LastSeen ?? DateTimeOffset.MinValue)
                .Take(count - (MaxTrackedSignatures * 3 / 4))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in toEvictKeys) _trackers.TryRemove(key, out _);
        }
    }

    private readonly record struct PathEntry(string Pattern, bool IsPrefix);

    private sealed class CachedPathPatterns(PathEntry[] loginPaths, PathEntry[] sensitivePaths)
    {
        public PathEntry[] LoginPaths { get; } = loginPaths;
        public PathEntry[] SensitivePaths { get; } = sensitivePaths;
    }

    private sealed class LoginTracker
    {
        private readonly object _lock = new();
        private readonly List<DateTimeOffset> _loginAttempts = new(8);

        public int FailedLoginCount;
        public int DirectPostCount;
        public DateTimeOffset? LastLoginPageView { get; set; }
        public DateTimeOffset? LastSuccessfulLogin { get; set; }
        public DateTimeOffset? LastSeen { get; set; }
        public string? LastLoginCountryCode { get; set; }
        public double BaselinePathDiversity { get; set; }

        public int TotalLoginAttempts
        {
            get { lock (_lock) return _loginAttempts.Count; }
        }

        public void RecordLoginAttempt(DateTimeOffset time)
        {
            lock (_lock) _loginAttempts.Add(time);
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
