using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Per-key violation tracker that backs <see cref="StickyDenyActionPolicy"/>.
///     A real implementation persists across process restarts (see
///     <c>SqliteStickyDenyStore</c>); the <see cref="InMemoryStickyDenyTracker"/>
///     in this file is for single-process deployments and tests.
/// </summary>
public interface IStickyDenyTracker
{
    /// <summary>
    ///     True when <paramref name="key"/> is currently inside an active
    ///     block window. Callers MUST NOT increment the violation counter
    ///     when this is true; otherwise a bot's retry storm extends the
    ///     block indefinitely.
    /// </summary>
    bool IsBlocked(string key, DateTimeOffset now);

    /// <summary>
    ///     Record one violation. Returns <c>true</c> when this violation
    ///     pushed the key from "under threshold" to "blocked". The caller
    ///     uses that signal to decide whether to short-circuit the request
    ///     with a 403 (true) or delegate to the under-threshold action
    ///     (false).
    /// </summary>
    bool RecordViolation(string key, DateTimeOffset now);
}

/// <summary>
///     Action policy that escalates to a hard-deny after the configured
///     number of violations within a window, and holds that deny for a
///     fixed TTL. Mirrors the "ban-on-Nth-violation" rules that
///     Cloudflare, Akamai, and AWS WAF rate-based rules expose.
///     <para>
///     Wires in cleanly as the <see cref="RateLimitActionOptions.OverLimitAction"/>
///     of a <see cref="RateLimitActionPolicy"/>: each bucket overflow
///     becomes one violation, the policy delegates to a fast-429 inner
///     action (typically <c>throttle-status</c>) until the threshold
///     trips, then takes over with a 403 + opaque marker cookie for the
///     block window.
///     </para>
///     <para>
///     The under-threshold action is resolved at request time via the
///     registry so operators can compose it with any other built-in
///     policy. The block phase itself is uncomposable -- when blocked the
///     policy returns 403 directly without consulting a fallback.
///     </para>
/// </summary>
public sealed class StickyDenyActionPolicy : IActionPolicy
{
    private readonly StickyDenyActionOptions _options;
    private readonly IStickyDenyTracker _tracker;
    private readonly IActionPolicyRegistry _registry;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<StickyDenyActionPolicy>? _logger;

    public StickyDenyActionPolicy(
        string name,
        StickyDenyActionOptions options,
        IStickyDenyTracker tracker,
        IActionPolicyRegistry registry,
        Func<DateTimeOffset>? clock = null,
        ILogger<StickyDenyActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger;
    }

    public string Name { get; }
    public ActionType ActionType => ActionType.Block;
    public PolicyIntent Intent => PolicyIntent.Block;

    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var key = ResolveKey(context, evidence, _options.KeyBy);
        if (key is null)
        {
            // No key resolvable -- fail open by delegating to the
            // under-threshold action. The alternative (routing every
            // visitor through one global violation counter) is worse
            // than passing the request through.
            return await DelegateUnderThreshold(context, evidence, cancellationToken);
        }

        var now = _clock();

        if (_tracker.IsBlocked(key, now))
        {
            // Already blocked. Do NOT increment -- a retry storm would
            // otherwise keep extending the block forever.
            _logger?.LogInformation(
                "Sticky-deny block in effect for {Path}: policy={Policy}",
                context.Request.Path, Name);
            return WriteBlockedResponse(context);
        }

        var nowBlocked = _tracker.RecordViolation(key, now);
        if (nowBlocked)
        {
            _logger?.LogWarning(
                "Sticky-deny threshold crossed for {Path}: policy={Policy}, key={KeyHash}",
                context.Request.Path, Name, HashForLog(key));
            return WriteBlockedResponse(context);
        }

        // Under threshold: delegate.
        return await DelegateUnderThreshold(context, evidence, cancellationToken);
    }

    private async Task<ActionResult> DelegateUnderThreshold(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.UnderThresholdAction))
            return ActionResult.Allowed("sticky-deny: no under-threshold action configured, fail open");

        var inner = _registry.GetPolicy(_options.UnderThresholdAction);
        if (inner is null)
        {
            _logger?.LogWarning(
                "Sticky-deny UnderThresholdAction '{Action}' missing from registry; failing open",
                _options.UnderThresholdAction);
            return ActionResult.Allowed("sticky-deny: under-threshold action not registered");
        }

        return await inner.ExecuteAsync(context, evidence, cancellationToken);
    }

    private ActionResult WriteBlockedResponse(HttpContext context)
    {
        context.Response.StatusCode = _options.BlockStatusCode;
        if (_options.SetCookie)
        {
            // Opaque marker cookie. The value is a random session-scoped
            // token -- the server is the source of truth, the cookie is
            // just an operator debug hint ("is this user actually
            // blocked?"). HttpOnly + Secure + SameSite=Strict so it
            // doesn't leak via JS or cross-origin requests.
            var marker = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
            context.Response.Cookies.Append(_options.CookieName, marker, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                MaxAge = TimeSpan.FromSeconds(Math.Max(1, _options.BlockTtlSeconds)),
            });
        }
        return ActionResult.Blocked(_options.BlockStatusCode,
            $"Sticky-deny by {Name}: block window active");
    }

    /// <summary>
    ///     Resolves the bucket key for the current request via the shared
    ///     <see cref="RateLimitKeyResolver"/>. Surfaced as <c>internal
    ///     static</c> because callers and tests pin the contract on the
    ///     StickyDeny side (not just rate-limit).
    /// </summary>
    internal static string? ResolveKey(HttpContext context, AggregatedEvidence evidence, RateLimitKey keyBy)
        => RateLimitKeyResolver.Resolve(context, evidence, keyBy);

    private static string HashForLog(string key)
    {
        // Short hex prefix is enough for log correlation without leaking the
        // raw key (which may contain identifying material like a signature).
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4);
    }
}

/// <summary>
///     Configuration for <see cref="StickyDenyActionPolicy"/>.
/// </summary>
public sealed class StickyDenyActionOptions
{
    /// <summary>
    ///     Number of violations within <see cref="WindowSeconds"/> that
    ///     pushes a key from "under threshold" to "blocked". Default 5.
    /// </summary>
    public int ViolationThreshold { get; set; } = 5;

    /// <summary>
    ///     Window over which violations accumulate toward the threshold.
    ///     Older violations age out. Default 60 seconds.
    /// </summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    ///     Block duration once the threshold trips. While inside this
    ///     window, every subsequent request returns
    ///     <see cref="BlockStatusCode"/> immediately and the violation
    ///     counter is NOT incremented (a bot's retries can't extend the
    ///     block). Default 300 seconds (5 minutes).
    /// </summary>
    public int BlockTtlSeconds { get; set; } = 300;

    /// <summary>
    ///     Which identity to bucket against. Same enum as
    ///     <see cref="RateLimitActionOptions.KeyBy"/>; defaults to
    ///     <see cref="RateLimitKey.Ip"/> because most sticky-deny
    ///     deployments target botnet rotation that's already aggregated
    ///     at the IP / subnet / ASN layer.
    /// </summary>
    public RateLimitKey KeyBy { get; set; } = RateLimitKey.Ip;

    /// <summary>
    ///     Policy name to delegate to when the violation count is under
    ///     the threshold. Typically <c>throttle-status</c> (fast 429 with
    ///     <c>Retry-After</c>). Empty or unknown delegates to allow.
    /// </summary>
    public string UnderThresholdAction { get; set; } = "throttle-status";

    /// <summary>
    ///     HTTP status code returned for sticky-deny blocks. Default 403.
    /// </summary>
    public int BlockStatusCode { get; set; } = 403;

    /// <summary>
    ///     Whether to set an opaque marker cookie when blocking. Useful
    ///     for operator debugging ("is this user actually blocked?") and
    ///     for fast client-side redirects on the block page. The cookie
    ///     value is server-generated random bytes; the server is the
    ///     source of truth.
    /// </summary>
    public bool SetCookie { get; set; } = true;

    /// <summary>
    ///     Name of the marker cookie. Default <c>sb_blocked</c>.
    /// </summary>
    public string CookieName { get; set; } = "sb_blocked";
}

/// <summary>
///     Single-process in-memory tracker. Loses state on restart. Suitable
///     for FOSS single-instance deployments and unit tests; production
///     multi-instance deployments should swap in a persistent tracker
///     (SQLite-backed <see cref="Mostlylucid.BotDetection.Storage.WriteBehindLfuStore{TKey,TValue,TWriteOp}"/>
///     subclass).
/// </summary>
public sealed class InMemoryStickyDenyTracker : IStickyDenyTracker
{
    private readonly StickyDenyActionOptions _options;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StickyDenyState> _state = new();

    public InMemoryStickyDenyTracker(StickyDenyActionOptions options)
    {
        _options = options;
    }

    public bool IsBlocked(string key, DateTimeOffset now)
    {
        if (!_state.TryGetValue(key, out var s)) return false;
        return s.BlockedUntilTicks > now.UtcTicks;
    }

    public bool RecordViolation(string key, DateTimeOffset now)
    {
        // CAS loop on the concurrent dict so concurrent violations on the
        // same key don't lose count.
        while (true)
        {
            if (!_state.TryGetValue(key, out var existing))
            {
                var fresh = new StickyDenyState(
                    FirstViolationTicks: now.UtcTicks,
                    LastViolationTicks: now.UtcTicks,
                    ViolationCount: 1,
                    BlockedUntilTicks: _options.ViolationThreshold <= 1
                        ? now.UtcTicks + TimeSpan.FromSeconds(_options.BlockTtlSeconds).Ticks
                        : 0);
                if (_state.TryAdd(key, fresh))
                    return fresh.BlockedUntilTicks > 0;
                continue; // raced; retry
            }

            // Age out the window: if the first violation is older than the
            // window, reset the counter to 1 (this violation).
            var windowStartTicks = now.UtcTicks - TimeSpan.FromSeconds(_options.WindowSeconds).Ticks;
            int newCount;
            long newFirst;
            if (existing.FirstViolationTicks < windowStartTicks)
            {
                newCount = 1;
                newFirst = now.UtcTicks;
            }
            else
            {
                newCount = existing.ViolationCount + 1;
                newFirst = existing.FirstViolationTicks;
            }

            var crossedThreshold = newCount >= _options.ViolationThreshold;
            var blockUntil = crossedThreshold
                ? now.UtcTicks + TimeSpan.FromSeconds(_options.BlockTtlSeconds).Ticks
                : existing.BlockedUntilTicks;

            var updated = new StickyDenyState(
                FirstViolationTicks: newFirst,
                LastViolationTicks: now.UtcTicks,
                ViolationCount: newCount,
                BlockedUntilTicks: blockUntil);

            if (_state.TryUpdate(key, updated, existing))
            {
                // Promotion only fires on the transition (was not blocked, now is).
                return existing.BlockedUntilTicks <= now.UtcTicks && crossedThreshold;
            }
            // CAS failed; retry.
        }
    }
}

/// <summary>
///     Immutable per-key state held by an <see cref="IStickyDenyTracker"/>.
/// </summary>
public sealed record StickyDenyState(
    long FirstViolationTicks,
    long LastViolationTicks,
    int ViolationCount,
    long BlockedUntilTicks);
