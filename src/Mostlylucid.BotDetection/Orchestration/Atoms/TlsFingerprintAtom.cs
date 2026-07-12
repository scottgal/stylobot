using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Definitions.TlsReference;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     SensorAtom (per Taxonomy.md) doing JA3 / JA4 TLS handshake
///     fingerprinting via edge-injected headers, plus corpus-driven
///     cipher-subset (damru) and version-delta (Multilogin / Kameleo)
///     checks against <see cref="IJa3ReferenceIndex"/>. Priority 11 --
///     Wave 0.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>TlsFingerprintContributor</c>.
///     </para>
///     <para>
///         KnownBotFingerprints + KnownBrowserFingerprints carried over
///         verbatim -- TODO YAML per feedback_no_word_lists.
///     </para>
///     <para>
///         Dual-header naming (X-Client-TLS-* + X-TLS-*) preserved so
///         operators using either the documented spec or the legacy
///         nginx shape keep signal flow.
///     </para>
/// </remarks>
public sealed partial class TlsFingerprintAtom : DetectorAtomBase
{
    // TODO: migrate to YAML per feedback_no_word_lists.
    private static readonly HashSet<string> KnownBotFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        "4e5f6b7c8d9e0a1b2c3d4e5f6a7b8c9d",
        "e7d1b9f8e7d1b9f8e7d1b9f8e7d1b9f8",
        "8d1c5e7f9a2b4d6e8c1a3b5d7f9e2c4a",
        "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6",
        "9f8e7d6c5b4a39281706f5e4d3c2b1a0",
        "b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9",
        "c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6"
    };

    private static readonly HashSet<string> KnownBrowserFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        "f4c3b2a1e9d8c7b6a5f4e3d2c1b0a9f8",
        "a9b8c7d6e5f4a3b2c1d0e9f8a7b6c5d4",
        "d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6",
        "e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0"
    };

    private readonly ILogger<TlsFingerprintAtom> _logger;
    private readonly IJa3ReferenceIndex? _referenceIndex;
    private readonly ITransportHeaderTrust? _transportTrust;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TlsFingerprintAtom(
        ILogger<TlsFingerprintAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        IJa3ReferenceIndex? referenceIndex = null,
        ITransportHeaderTrust? transportTrust = null)
        : base(name: "TlsFingerprint", category: "TLS")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _referenceIndex = referenceIndex;
        _transportTrust = transportTrust;
    }

    public override int Priority => 11;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double HttpConfidencePenalty => _configProvider.GetParameter(Name, "http_confidence_penalty", 0.05);
    private double KnownBotFingerprintConfidence => _configProvider.GetParameter(Name, "known_bot_fingerprint_confidence", 0.85);
    // A JA3 in the curated legitimate-browser set is a TRANSPORT-layer attestation of a real
    // browser: far harder to forge than headers (a scraper's TLS ClientHello yields a different
    // JA3, and a header-perfect spoof like adv-01-chrome-spoof carries a bot JA3, scored at
    // KnownBotFingerprintConfidence below). It is therefore weighted strongly enough to
    // counterbalance the behavioural automation signals (high rate / api-only / periodic) that a
    // legitimate SPA dashboard legitimately trips -- otherwise a real Chrome making a fast burst of
    // same-origin XHR reads as a bot. Bounded (a contribution, not a cap) and configurable; JA3
    // spoofing (curl-impersonate / utls) is the residual risk, mitigated by the small curated set +
    // the other signals still applying.
    private double KnownBrowserFingerprintConfidence => _configProvider.GetParameter(Name, "known_browser_fingerprint_confidence", -0.7);
    private double WeakCipherPenalty => _configProvider.GetParameter(Name, "weak_cipher_penalty", 0.4);
    private double ClientCertPenalty => _configProvider.GetParameter(Name, "client_cert_penalty", 0.3);
    private double OutdatedSslPenalty => _configProvider.GetParameter(Name, "outdated_ssl_penalty", 0.7);
    private double OldTlsPenalty => _configProvider.GetParameter(Name, "old_tls_penalty", 0.2);
    private int CipherSubsetMaxMissing => _configProvider.GetParameter(Name, "cipher_subset_max_missing", 4);
    private double CipherSubsetConfidence => _configProvider.GetParameter(Name, "cipher_subset_confidence", 0.85);
    private double CipherSubsetWeight => _configProvider.GetParameter(Name, "cipher_subset_weight", 1.5);
    private int VersionDeltaMinDelta => _configProvider.GetParameter(Name, "version_delta_min_delta", 2);
    private double VersionDeltaConfidence => _configProvider.GetParameter(Name, "version_delta_confidence", 0.55);
    private double VersionDeltaWeight => _configProvider.GetParameter(Name, "version_delta_weight", 1.1);
    private double SpoofedEdgeHeadersConfidence => _configProvider.GetParameter(Name, "spoofed_edge_headers_confidence", 0.3);
    private double SpoofedEdgeHeadersWeight => _configProvider.GetParameter(Name, "spoofed_edge_headers_weight", 1.2);
    private double WeightHumanSignal => _configProvider.GetDefaults(Name).Weights.HumanSignal;

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var contributions = new List<DetectionContribution>();
        var req = context.Request;

        try
        {
            var trustHeaders = true;
            if (_transportTrust is not null)
            {
                var trust = _transportTrust.Evaluate(context, sink, sessionId);
                trustHeaders = trust.Trusted;

                if (!trust.Trusted)
                {
                    var gatedHeaderPresent = req.Headers.ContainsKey("X-JA3-Hash")
                        || req.Headers.ContainsKey("X-JA3-String")
                        || req.Headers.ContainsKey("X-JA4")
                        || req.Headers.ContainsKey("X-JA4-Fingerprint")
                        || req.Headers.ContainsKey("X-JA4-Hash")
                        || req.Headers.ContainsKey("X-Client-TLS-Version")
                        || req.Headers.ContainsKey("X-TLS-Protocol")
                        || req.Headers.ContainsKey("X-Client-TLS-Cipher")
                        || req.Headers.ContainsKey("X-TLS-Cipher");

                    if (gatedHeaderPresent)
                    {
                        sink.Raise($"{SignalKeys.TransportSpoofedEdgeHeaders}:true", sessionId);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = SpoofedEdgeHeadersConfidence,
                            Weight = SpoofedEdgeHeadersWeight,
                            Reason = "Edge TLS fingerprint headers from an untrusted direct peer (possible spoof)",
                            BotType = BotType.Scraper.ToString()
                        });
                    }
                }
            }

            var isHttps = req.IsHttps;
            if (!isHttps
                && req.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto)
                && forwardedProto.ToString().Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                isHttps = true;
                sink.Raise("tls.behind_proxy:true", sessionId);
            }

            sink.Raise($"{SignalKeys.TlsIsHttps}:{(isHttps ? "true" : "false")}", sessionId);

            if (!isHttps)
            {
                sink.Raise($"{SignalKeys.TlsAvailable}:false", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = HttpConfidencePenalty,
                    Weight = 0.3,
                    Reason = "Using HTTP instead of HTTPS (uncommon for modern browsers)",
                    BotType = BotType.Unknown.ToString()
                });
                return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
            }

            sink.Raise($"{SignalKeys.TlsAvailable}:true", sessionId);

            var protocol = trustHeaders
                ? (req.Headers["X-Client-TLS-Version"].FirstOrDefault()
                   ?? req.Headers["X-TLS-Protocol"].FirstOrDefault())
                : null;
            if (!string.IsNullOrEmpty(protocol))
            {
                sink.Raise($"{SignalKeys.TlsProtocol}:{protocol}", sessionId);
                AnalyzeTlsProtocol(protocol, contributions);
            }

            var cipher = trustHeaders
                ? (req.Headers["X-Client-TLS-Cipher"].FirstOrDefault()
                   ?? req.Headers["X-TLS-Cipher"].FirstOrDefault())
                : null;
            if (!string.IsNullOrEmpty(cipher))
            {
                sink.Raise($"tls.cipher_suite:{cipher}", sessionId);
                AnalyzeCipherSuite(cipher, contributions);
            }

            if (trustHeaders) ReadJa4Fingerprint(context, sink, sessionId);

            var ja3Hash = trustHeaders ? GetJa3Fingerprint(context, sink, sessionId) : string.Empty;
            if (!string.IsNullOrEmpty(ja3Hash))
            {
                if (KnownBotFingerprints.Contains(ja3Hash))
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = KnownBotFingerprintConfidence,
                        Weight = 1.8,
                        Reason = $"Known bot TLS fingerprint detected: {ja3Hash[..Math.Min(8, ja3Hash.Length)]}...",
                        BotType = BotType.Scraper.ToString()
                    });
                }
                else if (KnownBrowserFingerprints.Contains(ja3Hash))
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = KnownBrowserFingerprintConfidence,
                        Weight = WeightHumanSignal * 1.5,
                        Reason = $"Known legitimate browser fingerprint: {ja3Hash[..Math.Min(8, ja3Hash.Length)]}..."
                    });
                }
                else
                {
                    sink.Raise("tls.fingerprint_known:false", sessionId);
                }
            }

            RunCorpusChecks(context, sink, sessionId, contributions);

            var tlsFeature = context.Features.Get<ITlsConnectionFeature>();
            if (tlsFeature?.ClientCertificate is not null)
            {
                sink.Raise("tls.client_cert_present:true", sessionId);
                sink.Raise($"tls.client_cert_issuer:{tlsFeature.ClientCertificate.Issuer}", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = ClientCertPenalty,
                    Weight = 1.2,
                    Reason = "Client certificate authentication used (uncommon for browsers)",
                    BotType = BotType.Unknown.ToString()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing TLS fingerprint");
        }

        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.15,
                Weight = WeightHumanSignal,
                Reason = "TLS connection appears normal"
            });
        }

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private void AnalyzeTlsProtocol(string protocol, List<DetectionContribution> contributions)
    {
        if (protocol.Contains("SSL", StringComparison.OrdinalIgnoreCase))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = OutdatedSslPenalty,
                Weight = 1.5,
                Reason = $"Outdated SSL protocol: {protocol}",
                BotType = BotType.Scraper.ToString()
            });
        }
        else if (protocol.Contains("TLSv1.0", StringComparison.OrdinalIgnoreCase)
                 || protocol.Contains("TLSv1.1", StringComparison.OrdinalIgnoreCase))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = OldTlsPenalty,
                Weight = 0.8,
                Reason = $"Old TLS version: {protocol} (modern browsers use TLS 1.2+)",
                BotType = BotType.Unknown.ToString()
            });
        }
    }

    private void AnalyzeCipherSuite(string cipherSuite, List<DetectionContribution> contributions)
    {
        if (cipherSuite.Contains("NULL", StringComparison.OrdinalIgnoreCase)
            || cipherSuite.Contains("NONE", StringComparison.OrdinalIgnoreCase)
            || cipherSuite.Contains("MD5", StringComparison.OrdinalIgnoreCase))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = WeakCipherPenalty,
                Weight = 1.3,
                Reason = $"Weak cipher suite detected: {cipherSuite}",
                BotType = BotType.Unknown.ToString()
            });
        }

        if (cipherSuite.Contains("DES", StringComparison.OrdinalIgnoreCase)
            || cipherSuite.Contains("EXPORT", StringComparison.OrdinalIgnoreCase))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = OutdatedSslPenalty - 0.1,
                Weight = 1.4,
                Reason = "Export-grade or DES cipher (very outdated)",
                BotType = BotType.Scraper.ToString()
            });
        }
    }

    private static void ReadJa4Fingerprint(HttpContext context, SignalSink sink, string sessionId)
    {
        string? ja4 = null;
        if (context.Request.Headers.TryGetValue("X-JA4", out var ja4Header))
            ja4 = ja4Header.ToString();
        else if (context.Request.Headers.TryGetValue("X-JA4-Fingerprint", out var ja4FpHeader))
            ja4 = ja4FpHeader.ToString();
        else if (context.Request.Headers.TryGetValue("X-JA4-Hash", out var ja4HashHeader))
            ja4 = ja4HashHeader.ToString();

        if (!string.IsNullOrWhiteSpace(ja4))
        {
            sink.Raise($"tls.ja4:{ja4}", sessionId);
            sink.Raise($"tls.ja4_hash:{ja4}", sessionId);
        }
    }

    private string GetJa3Fingerprint(HttpContext context, SignalSink sink, string sessionId)
    {
        if (context.Request.Headers.TryGetValue("X-JA3-Hash", out var ja3Hash))
        {
            var hash = ja3Hash.ToString();
            sink.Raise($"tls.ja3_hash:{hash}", sessionId);
            return hash;
        }

        if (context.Request.Headers.TryGetValue("X-JA3-String", out var ja3String))
        {
            var str = ja3String.ToString();
            sink.Raise($"tls.ja3_string:{str}", sessionId);
            var hash = ComputeMd5Hash(str);
            sink.Raise($"tls.ja3_hash:{hash}", sessionId);
            return hash;
        }

        return string.Empty;
    }

    private static string ComputeMd5Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void RunCorpusChecks(HttpContext context, SignalSink sink, string sessionId, List<DetectionContribution> contributions)
    {
        if (_referenceIndex is null || _referenceIndex.Count == 0) return;

        var ua = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(ua)) return;

        var claim = ParseUaBrowserClaim(ua);
        if (claim is null) return;

        var observedJa3String = sink.ReadHint("tls.ja3_string");
        if (!string.IsNullOrEmpty(observedJa3String))
        {
            var reference = _referenceIndex.GetReference(claim.Browser, claim.Version, claim.Mobile);
            if (reference is not null && IsStrictCipherSubset(observedJa3String, reference.Ja3String, out var missing))
            {
                sink.Raise($"{SignalKeys.TlsCipherSubsetOfRealChrome}:true", sessionId);
                sink.Raise($"{SignalKeys.TlsCipherSubsetMissingCount}:{missing}", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = CipherSubsetConfidence,
                    Weight = CipherSubsetWeight,
                    Reason = $"TLS cipher list is strict subset of real {claim.Browser} {reference.Version} ({missing} ciphers missing)",
                    BotType = BotType.Scraper.ToString()
                });
            }
        }

        var observedJa3Hash = sink.ReadHint("tls.ja3_hash");
        if (!string.IsNullOrEmpty(observedJa3Hash))
        {
            var match = _referenceIndex.MatchAnyVersion(observedJa3Hash);
            if (match is not null
                && match.Browser.Equals(claim.Browser, StringComparison.OrdinalIgnoreCase)
                && match.Mobile == claim.Mobile
                && (claim.Version - match.Version) >= VersionDeltaMinDelta)
            {
                sink.Raise($"{SignalKeys.TlsVersionDeltaFromUa}:{claim.Version - match.Version}", sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = VersionDeltaConfidence,
                    Weight = VersionDeltaWeight,
                    Reason = $"TLS version delta: UA claims {claim.Browser} {claim.Version} but TLS matches {claim.Browser} {match.Version}",
                    BotType = BotType.Scraper.ToString()
                });
            }
        }
    }

    private sealed record UaBrowserClaim(string Browser, int Version, bool Mobile);

    private static UaBrowserClaim? ParseUaBrowserClaim(string userAgent)
    {
        var mobile = userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
                     || userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase);
        var edge = EdgeRegex().Match(userAgent);
        if (edge.Success && int.TryParse(edge.Groups[1].Value, out var edgeVer))
            return new UaBrowserClaim("edge", edgeVer, mobile);
        var opera = OperaRegex().Match(userAgent);
        if (opera.Success && int.TryParse(opera.Groups[1].Value, out var operaVer))
            return new UaBrowserClaim("opera", operaVer, mobile);
        var chrome = ChromeRegex().Match(userAgent);
        if (chrome.Success && int.TryParse(chrome.Groups[1].Value, out var chromeVer))
            return new UaBrowserClaim("chrome", chromeVer, mobile);
        var firefox = FirefoxRegex().Match(userAgent);
        if (firefox.Success && int.TryParse(firefox.Groups[1].Value, out var firefoxVer))
            return new UaBrowserClaim("firefox", firefoxVer, mobile);
        var safari = SafariVersionRegex().Match(userAgent);
        if (safari.Success && userAgent.Contains("Safari", StringComparison.Ordinal)
            && !userAgent.Contains("Chrome", StringComparison.Ordinal)
            && int.TryParse(safari.Groups[1].Value, out var safariVer))
            return new UaBrowserClaim("safari", safariVer, mobile);
        return null;
    }

    private bool IsStrictCipherSubset(string observed, string reference, out int missingCount)
    {
        missingCount = 0;
        var oParts = observed.Split(',');
        var rParts = reference.Split(',');
        if (oParts.Length != 5 || rParts.Length != 5) return false;
        if (oParts[0] != rParts[0] || oParts[2] != rParts[2]
            || oParts[3] != rParts[3] || oParts[4] != rParts[4]) return false;

        var oSet = oParts[1].Split('-').ToHashSet();
        var rSet = rParts[1].Split('-').ToHashSet();
        foreach (var c in oSet)
            if (!rSet.Contains(c)) return false;
        missingCount = rSet.Count - oSet.Count;
        return missingCount >= 1 && missingCount <= CipherSubsetMaxMissing;
    }

    [GeneratedRegex(@"Edg(?:e)?/(\d+)")]
    private static partial Regex EdgeRegex();

    [GeneratedRegex(@"OPR/(\d+)")]
    private static partial Regex OperaRegex();

    [GeneratedRegex(@"Chrome/(\d+)")]
    private static partial Regex ChromeRegex();

    [GeneratedRegex(@"Firefox/(\d+)")]
    private static partial Regex FirefoxRegex();

    [GeneratedRegex(@"Version/(\d+)")]
    private static partial Regex SafariVersionRegex();
}
