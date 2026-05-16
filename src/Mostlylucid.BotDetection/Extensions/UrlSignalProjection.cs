using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Projects bot-detection signals onto outbound query-string params, with
///     optional HMAC signing. Mirrors <see cref="YarpExtensions.AddBotDetectionHeaders"/>
///     but in URL form so downstream CDNs can cache per-signal without a
///     <c>Vary</c> header.
///
///     <para>
///     The signing canonical form is the alphabetically-sorted set of
///     <c>name=value</c> pairs joined by <c>&amp;</c>. Receivers re-sort before
///     comparing, so wire order does not matter. The <c>sb_sig</c> param itself
///     is excluded from the canonical input.
///     </para>
///
///     <para>
///     Security model lives on <see cref="UrlRewriteOptions"/>. The short
///     version: every emitted param is namespaced with <see cref="UrlRewriteOptions.Prefix"/>,
///     pre-existing prefixed params are stripped by the caller, and the HMAC is
///     keyed on <c>BotDetection:SignatureHashKey</c>.
///     </para>
/// </summary>
public static class UrlSignalProjection
{
    /// <summary>Name of the signing param appended when <see cref="UrlRewriteOptions.Sign"/> is true.</summary>
    public const string SignatureParamSuffix = "sig";

    /// <summary>
    ///     Project the configured signals onto query-string params. Each emitted
    ///     param is delivered via <paramref name="addParam"/> as
    ///     <c>(name, value)</c>; the caller decides whether that's a YARP
    ///     <c>QueryStringTransform</c>, a <c>QueryBuilder</c>, or anything else.
    ///     A missing signal is skipped (not emitted as empty) so the signing
    ///     canonical form stays tied to present values only.
    /// </summary>
    /// <param name="httpContext">The current request after detection has run.</param>
    /// <param name="options">Configured options; if <see cref="UrlRewriteOptions.Enabled"/> is false this is a no-op.</param>
    /// <param name="signingKey">Pre-resolved HMAC key (UTF-8 bytes of <c>SignatureHashKey</c>). Required when <see cref="UrlRewriteOptions.Sign"/> is true.</param>
    /// <param name="addParam">Called once per (name, value) including the trailing signature param.</param>
    public static void AddBotDetectionQueryParams(
        this HttpContext httpContext,
        UrlRewriteOptions options,
        byte[]? signingKey,
        Action<string, string> addParam)
    {
        if (!options.Enabled) return;

        var evidence = ResolveEvidence(httpContext);
        var prefix = options.Prefix ?? "sb_";

        // SortedDictionary so the canonical signing input is order-independent.
        var emitted = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var signalName in options.Signals)
        {
            if (string.IsNullOrWhiteSpace(signalName)) continue;
            var value = ProjectSignal(signalName, httpContext, evidence);
            if (value is null) continue;
            emitted[prefix + signalName] = value;
        }

        if (emitted.Count == 0) return;

        if (options.Sign)
        {
            if (signingKey is null || signingKey.Length == 0)
                throw new InvalidOperationException(
                    "UrlRewriteOptions.Sign=true but no signing key supplied. " +
                    "Set BotDetection:SignatureHashKey or disable signing.");

            var sig = ComputeSignature(emitted, signingKey);
            emitted[prefix + SignatureParamSuffix] = sig;
        }

        foreach (var kvp in emitted)
            addParam(kvp.Key, kvp.Value);
    }

    /// <summary>
    ///     Verify a signed param set on the receiving end. Returns true when the
    ///     <c>{prefix}sig</c> param is present and matches the HMAC of the other
    ///     prefixed params. Use this in your upstream's request-pipeline guard.
    /// </summary>
    public static bool VerifySignedQueryParams(
        IEnumerable<KeyValuePair<string, string>> queryParams,
        string prefix,
        byte[] signingKey)
    {
        if (signingKey is null || signingKey.Length == 0)
            throw new ArgumentException("Signing key must be supplied.", nameof(signingKey));

        var sigParamName = prefix + SignatureParamSuffix;
        string? presentedSig = null;
        var emitted = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var kvp in queryParams)
        {
            if (!kvp.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (kvp.Key.Equals(sigParamName, StringComparison.Ordinal))
            {
                presentedSig = kvp.Value;
                continue;
            }
            emitted[kvp.Key] = kvp.Value;
        }

        if (presentedSig is null || emitted.Count == 0) return false;

        var expected = ComputeSignature(emitted, signingKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(presentedSig));
    }

    /// <summary>
    ///     Match a request path against the include/exclude rules.
    ///     Pattern syntax:
    ///     <list type="bullet">
    ///         <item><c>/prefix/*</c> — path starts with <c>/prefix/</c> (any depth).</item>
    ///         <item><c>*.ext</c> — path ends with <c>.ext</c>.</item>
    ///         <item><c>/exact</c> — exact match.</item>
    ///     </list>
    /// </summary>
    public static bool ShouldRewrite(PathString path, UrlRewriteOptions options)
    {
        if (!options.Enabled) return false;

        if (options.PathExclusions is { Count: > 0 } excludes)
        {
            foreach (var pattern in excludes)
                if (MatchesPattern(path, pattern)) return false;
        }

        if (options.ApplyTo == UrlRewriteScope.All) return true;

        if (options.PathPatterns is { Count: > 0 } includes)
        {
            foreach (var pattern in includes)
                if (MatchesPattern(path, pattern)) return true;
        }

        return false;
    }

    private static bool MatchesPattern(PathString path, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        var pathStr = path.HasValue ? path.Value! : "/";

        // Wildcard suffix: /prefix/* matches any path under /prefix/
        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            return path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Wildcard prefix: *.ext matches any path ending in .ext
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return pathStr.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        // Exact match
        return string.Equals(pathStr, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static AggregatedEvidence? ResolveEvidence(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var raw)
            && raw is AggregatedEvidence evidence)
            return evidence;
        return null;
    }

    private static string? ProjectSignal(string name, HttpContext ctx, AggregatedEvidence? evidence)
    {
        switch (name)
        {
            case "is_bot":
                return ctx.IsBot() ? "true" : "false";

            case "probability":
                return evidence?.BotProbability.ToString("F3", CultureInfo.InvariantCulture);

            case "risk_band":
                return evidence?.RiskBand.ToString();

            case "bot_type":
                return ctx.GetBotType()?.ToString();

            case "bot_name":
                return ctx.GetBotName();

            case "country":
                if (TryGetSignal<string>(evidence, SignalKeys.GeoCountryCode, out var cc)
                    && !string.IsNullOrEmpty(cc) && cc != "LOCAL" && cc != "XX")
                    return cc;
                var headerCountry = ctx.Request.Headers["CF-IPCountry"].FirstOrDefault()
                                    ?? ctx.Request.Headers["X-Country"].FirstOrDefault();
                return headerCountry is null or "" or "XX" or "LOCAL" ? null : headerCountry;

            case "is_vpn":
                return BoolSignal(evidence, SignalKeys.GeoIsVpn);
            case "is_proxy":
                return BoolSignal(evidence, SignalKeys.GeoIsProxy);
            case "is_tor":
                return BoolSignal(evidence, SignalKeys.GeoIsTor);
            case "is_datacenter":
                return BoolSignal(evidence, SignalKeys.GeoIsHosting)
                       ?? BoolSignal(evidence, SignalKeys.IpIsDatacenter);

            case "fingerprint_id":
                return TryGetSignal<string>(evidence, SignalKeys.IdentityFingerprintId, out var fp) ? fp : null;

            case "client_type":
                return TryGetSignal<string>(evidence, SignalKeys.IdentityClientType, out var clientType) ? clientType : null;

            case "threat_score":
                return TryGetSignal<double>(evidence, SignalKeys.HoneypotThreatScore, out var ts)
                    ? ts.ToString("F3", CultureInfo.InvariantCulture)
                    : null;

            default:
                // Unknown projector — silently skip. Caller's config typo
                // shouldn't produce an empty param that breaks signing.
                return null;
        }
    }

    private static string? BoolSignal(AggregatedEvidence? evidence, string key)
        => TryGetSignal<bool>(evidence, key, out var v) ? (v ? "true" : "false") : null;

    private static bool TryGetSignal<T>(AggregatedEvidence? evidence, string key, out T value)
    {
        value = default!;
        if (evidence?.Signals is null) return false;
        if (!evidence.Signals.TryGetValue(key, out var raw) || raw is null) return false;
        if (raw is T direct) { value = direct; return true; }
        try
        {
            value = (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSignature(IReadOnlyDictionary<string, string> emitted, byte[] key)
    {
        // Canonical form: name=value joined by &, with names already sorted (caller passes SortedDictionary)
        var sb = new StringBuilder();
        var first = true;
        foreach (var kvp in emitted)
        {
            if (!first) sb.Append('&');
            sb.Append(kvp.Key).Append('=').Append(kvp.Value);
            first = false;
        }

        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash)[..32]; // 128-bit truncation — plenty against forgery, short on the wire
    }
}
