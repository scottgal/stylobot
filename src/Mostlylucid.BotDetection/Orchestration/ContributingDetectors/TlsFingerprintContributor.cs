using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Definitions.TlsReference;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     TLS fingerprinting contributor using JA3/JA4-style fingerprinting.
///     Analyzes TLS handshake parameters to detect automated clients.
///     Best-in-breed approach:
///     - JA3: TLS client hello fingerprinting (SSLVersion,Ciphers,Extensions,EllipticCurves,EllipticCurvePointFormats)
///     - JA4: Modern evolution with better normalization
///     FUTURE ENHANCEMENT: Integrate with ThreatFox (https://threatfox.abuse.ch/export/json/recent/)
///     for a maintained database of malicious TLS fingerprints. Current implementation uses sample fingerprints.
///     - Detects headless browsers, automation frameworks, and custom HTTP clients
///     IMPORTANT: This contributor relies on reverse proxy (nginx/HAProxy) to extract
///     TLS handshake data and pass via headers (X-JA3-Hash, X-TLS-Protocol, X-TLS-Cipher).
///     ASP.NET Core's ITlsConnectionFeature has very limited TLS information available.
///
///     Configuration loaded from: tls.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:TlsFingerprintContributor:*
/// </summary>
public partial class TlsFingerprintContributor : ConfiguredContributorBase
{
    // Known bot TLS fingerprints (JA3 MD5 hashes)
    // These are sample fingerprints - in production, maintain a database
    private static readonly HashSet<string> KnownBotFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        // cURL fingerprints
        "4e5f6b7c8d9e0a1b2c3d4e5f6a7b8c9d",
        "e7d1b9f8e7d1b9f8e7d1b9f8e7d1b9f8",

        // Python requests library
        "8d1c5e7f9a2b4d6e8c1a3b5d7f9e2c4a",

        // Go net/http
        "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6",

        // Headless Chrome automation fingerprints (differ from normal Chrome)
        "9f8e7d6c5b4a39281706f5e4d3c2b1a0",
        "b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9",

        // Selenium/WebDriver fingerprints
        "c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6"
    };

    // Known legitimate browser fingerprint patterns
    private static readonly HashSet<string> KnownBrowserFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chrome desktop
        "f4c3b2a1e9d8c7b6a5f4e3d2c1b0a9f8",
        "a9b8c7d6e5f4a3b2c1d0e9f8a7b6c5d4",

        // Firefox
        "d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6",

        // Safari/WebKit
        "e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0"
    };

    private readonly ILogger<TlsFingerprintContributor> _logger;
    private readonly IJa3ReferenceIndex? _referenceIndex;

    public TlsFingerprintContributor(
        ILogger<TlsFingerprintContributor> logger,
        IDetectorConfigProvider configProvider,
        IJa3ReferenceIndex? referenceIndex = null)
        : base(configProvider)
    {
        _logger = logger;
        _referenceIndex = referenceIndex;
    }

    public override string Name => "TlsFingerprint";
    public override int Priority => Manifest?.Priority ?? 11;

    // No triggers - runs in first wave
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters from YAML
    private double HttpConfidencePenalty => GetParam("http_confidence_penalty", 0.05);
    private double KnownBotFingerprintConfidence => GetParam("known_bot_fingerprint_confidence", 0.85);
    private double KnownBrowserFingerprintConfidence => GetParam("known_browser_fingerprint_confidence", -0.15);
    private double WeakCipherPenalty => GetParam("weak_cipher_penalty", 0.4);
    private double ClientCertPenalty => GetParam("client_cert_penalty", 0.3);
    private double OutdatedSslPenalty => GetParam("outdated_ssl_penalty", 0.7);
    private double OldTlsPenalty => GetParam("old_tls_penalty", 0.2);
    // damru cipher-blacklist subset detection: how many missing ciphers we accept
    // before treating the subset as "too different to be a deliberate blacklist".
    private int CipherSubsetMaxMissing => GetParam("cipher_subset_max_missing", 4);
    private double CipherSubsetConfidence => GetParam("cipher_subset_confidence", 0.85);
    private double CipherSubsetWeight => GetParam("cipher_subset_weight", 1.5);
    // UA-vs-TLS version delta: how many versions behind the UA claim before flagging.
    private int VersionDeltaMinDelta => GetParam("version_delta_min_delta", 2);
    private double VersionDeltaConfidence => GetParam("version_delta_confidence", 0.55);
    private double VersionDeltaWeight => GetParam("version_delta_weight", 1.1);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            // Check if TLS connection (https://)
            // Behind a reverse proxy (e.g. Caddy, nginx), the backend connection is plain HTTP.
            // Check X-Forwarded-Proto header first for the original client scheme.
            var isHttps = state.HttpContext.Request.IsHttps;

            if (!isHttps
                && state.HttpContext.Request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto)
                && forwardedProto.ToString().Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                isHttps = true;
                state.WriteSignal("tls.behind_proxy", true);
            }

            state.WriteSignal("tls.is_https", isHttps);

            if (!isHttps)
            {
                // HTTP (not HTTPS) - slight bot indicator
                state.WriteSignal("tls.available", false);
                contributions.Add(BotContribution(
                    "TLS",
                    "Using HTTP instead of HTTPS (uncommon for modern browsers)",
                    confidenceOverride: HttpConfidencePenalty,
                    weightMultiplier: 0.3));
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            state.WriteSignal("tls.available", true);

            // Get TLS protocol. Accept both the documented public name
            // (X-Client-TLS-Version - used by DetectionBroadcastMiddleware and recommended in
            // docs/REVERSE_PROXY_SIGNALS.md) and the contributor's legacy nginx-shaped name
            // (X-TLS-Protocol). Both conventions appear in the wild; reading either keeps the
            // signal flowing regardless of which one the operator configured at the edge.
            string? protocol = state.HttpContext.Request.Headers["X-Client-TLS-Version"].FirstOrDefault()
                            ?? state.HttpContext.Request.Headers["X-TLS-Protocol"].FirstOrDefault();
            if (!string.IsNullOrEmpty(protocol))
            {
                state.WriteSignal(SignalKeys.TlsProtocol, protocol);
                AnalyzeTlsProtocol(protocol, contributions, state);
            }

            // Cipher suite. Same dual-name rule as TLS protocol.
            string? cipher = state.HttpContext.Request.Headers["X-Client-TLS-Cipher"].FirstOrDefault()
                          ?? state.HttpContext.Request.Headers["X-TLS-Cipher"].FirstOrDefault();
            if (!string.IsNullOrEmpty(cipher))
            {
                state.WriteSignal("tls.cipher_suite", cipher);
                AnalyzeCipherSuite(cipher, contributions, state);
            }

            // Get JA4 fingerprint from reverse proxy (CF Bot Management Enterprise,
            // Caddy ja4 plugin, custom edges). No "known JA4" list yet so this is
            // signal-emit only; downstream consumers (IdentityVectorContributor,
            // LearningTriggers) pick it up via tls.ja4 / tls.ja4_hash.
            ReadJa4Fingerprint(state.HttpContext, state);

            // Get JA3 fingerprint from reverse proxy
            var ja3Hash = GetJa3Fingerprint(state.HttpContext, state);
            if (!string.IsNullOrEmpty(ja3Hash))
            {
                // Check against known fingerprints
                if (KnownBotFingerprints.Contains(ja3Hash))
                    contributions.Add(BotContribution(
                        "TLS",
                        $"Known bot TLS fingerprint detected: {ja3Hash[..Math.Min(8, ja3Hash.Length)]}...",
                        confidenceOverride: KnownBotFingerprintConfidence,
                        weightMultiplier: 1.8,
                        botType: BotType.Scraper.ToString()));
                else if (KnownBrowserFingerprints.Contains(ja3Hash))
                    contributions.Add(HumanContribution(
                        "TLS",
                        $"Known legitimate browser fingerprint: {ja3Hash[..Math.Min(8, ja3Hash.Length)]}...")
                        with
                        {
                            ConfidenceDelta = KnownBrowserFingerprintConfidence,
                            Weight = WeightHumanSignal * 1.5
                        });
                else
                    state.WriteSignal("tls.fingerprint_known", false);
            }

            // Corpus-driven checks: cipher-subset (damru) + version-delta
            // (Multilogin / Kameleo Chroma). Only run when a corpus is loaded
            // and we have at least a JA3 string or hash to compare.
            RunCorpusChecks(state, contributions);

            // Check for client certificate (uncommon for browsers)
            var tlsFeature = state.HttpContext.Features.Get<ITlsConnectionFeature>();
            if (tlsFeature?.ClientCertificate != null)
            {
                state.WriteSignal("tls.client_cert_present", true);
                state.WriteSignal("tls.client_cert_issuer", tlsFeature.ClientCertificate.Issuer);

                contributions.Add(BotContribution(
                    "TLS",
                    "Client certificate authentication used (uncommon for browsers)",
                    confidenceOverride: ClientCertPenalty,
                    weightMultiplier: 1.2));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing TLS fingerprint");
            state.WriteSignal("tls.error", ex.Message);
        }

        // If no contributions yet, add neutral
        if (contributions.Count == 0)
            contributions.Add(HumanContribution(
                "TLS",
                "TLS connection appears normal"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private void AnalyzeTlsProtocol(string protocol, List<DetectionContribution> contributions,
        BlackboardState state)
    {
        // Outdated protocols are suspicious
        if (protocol.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            contributions.Add(BotContribution(
                "TLS",
                $"Outdated SSL protocol: {protocol}",
                confidenceOverride: OutdatedSslPenalty,
                weightMultiplier: 1.5,
                botType: BotType.Scraper.ToString()));
        else if (protocol.Contains("TLSv1.0", StringComparison.OrdinalIgnoreCase) ||
                 protocol.Contains("TLSv1.1", StringComparison.OrdinalIgnoreCase))
            contributions.Add(BotContribution(
                "TLS",
                $"Old TLS version: {protocol} (modern browsers use TLS 1.2+)",
                confidenceOverride: OldTlsPenalty,
                weightMultiplier: 0.8));
        // TLS 1.2+ is normal
    }

    private void AnalyzeCipherSuite(string cipherSuite, List<DetectionContribution> contributions,
        BlackboardState state)
    {
        // Weak ciphers indicate old/custom clients
        if (cipherSuite.Contains("NULL", StringComparison.OrdinalIgnoreCase) ||
            cipherSuite.Contains("NONE", StringComparison.OrdinalIgnoreCase) ||
            cipherSuite.Contains("MD5", StringComparison.OrdinalIgnoreCase))
            contributions.Add(BotContribution(
                "TLS",
                $"Weak cipher suite detected: {cipherSuite}",
                confidenceOverride: WeakCipherPenalty,
                weightMultiplier: 1.3));

        // Export-grade or DES ciphers
        if (cipherSuite.Contains("DES", StringComparison.OrdinalIgnoreCase) ||
            cipherSuite.Contains("EXPORT", StringComparison.OrdinalIgnoreCase))
            contributions.Add(BotContribution(
                "TLS",
                "Export-grade or DES cipher (very outdated)",
                confidenceOverride: OutdatedSslPenalty - 0.1, // Slightly less than full SSL
                weightMultiplier: 1.4,
                botType: BotType.Scraper.ToString()));
    }

    private static void ReadJa4Fingerprint(HttpContext context, BlackboardState state)
    {
        // JA4 is a structured fingerprint (e.g. t13d1516h2_8daaf6152771_b0da82dd1658)
        // produced by the edge. Unlike JA3 there's no separate hash form - the
        // fingerprint string itself is the canonical identifier - so we write the
        // same value to both tls.ja4 (consumed by IdentityVectorContributor) and
        // tls.ja4_hash (monitored by LearningTriggers).
        string? ja4 = null;
        if (context.Request.Headers.TryGetValue("X-JA4", out var ja4Header))
            ja4 = ja4Header.ToString();
        else if (context.Request.Headers.TryGetValue("X-JA4-Fingerprint", out var ja4FpHeader))
            ja4 = ja4FpHeader.ToString();
        else if (context.Request.Headers.TryGetValue("X-JA4-Hash", out var ja4HashHeader))
            ja4 = ja4HashHeader.ToString();

        if (!string.IsNullOrWhiteSpace(ja4))
        {
            state.WriteSignal("tls.ja4", ja4);
            state.WriteSignal("tls.ja4_hash", ja4);
        }
    }

    private string GetJa3Fingerprint(HttpContext context, BlackboardState state)
    {
        // Check for JA3 hash from reverse proxy (e.g., nginx with ssl_ja3 module)
        if (context.Request.Headers.TryGetValue("X-JA3-Hash", out var ja3Hash))
        {
            var hash = ja3Hash.ToString();
            state.WriteSignal("tls.ja3_hash", hash);
            return hash;
        }

        // Check for JA3 string if hash not available
        if (context.Request.Headers.TryGetValue("X-JA3-String", out var ja3String))
        {
            var str = ja3String.ToString();
            state.WriteSignal("tls.ja3_string", str);

            // Compute hash from string
            var hash = ComputeMd5Hash(str);
            state.WriteSignal("tls.ja3_hash", hash);
            return hash;
        }

        // No JA3 data available
        return string.Empty;
    }

    private static string ComputeMd5Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // === Corpus-driven checks: subset + version-delta =======================

    private void RunCorpusChecks(BlackboardState state, List<DetectionContribution> contributions)
    {
        if (_referenceIndex == null || _referenceIndex.Count == 0) return;

        var ua = state.HttpContext.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(ua)) return;

        var claim = ParseUaBrowserClaim(ua);
        if (claim == null) return;

        // Subset check: needs the observed JA3 STRING (parts split).
        var observedJa3String = state.GetSignal<string>("tls.ja3_string");
        if (!string.IsNullOrEmpty(observedJa3String))
        {
            var reference = _referenceIndex.GetReference(claim.Browser, claim.Version, claim.Mobile);
            if (reference != null && IsStrictCipherSubset(observedJa3String, reference.Ja3String, out var missing))
            {
                state.WriteSignal(SignalKeys.TlsCipherSubsetOfRealChrome, true);
                state.WriteSignal(SignalKeys.TlsCipherSubsetMissingCount, missing);
                contributions.Add(BotContribution(
                    "TLS",
                    $"TLS cipher list is strict subset of real {claim.Browser} {reference.Version} ({missing} ciphers missing)",
                    confidenceOverride: CipherSubsetConfidence,
                    weightMultiplier: CipherSubsetWeight,
                    botType: BotType.Scraper.ToString()));
            }
        }

        // Version-delta check: needs the observed JA3 HASH for the corpus lookup.
        var observedJa3Hash = state.GetSignal<string>("tls.ja3_hash");
        if (!string.IsNullOrEmpty(observedJa3Hash))
        {
            var match = _referenceIndex.MatchAnyVersion(observedJa3Hash);
            if (match != null
                && match.Browser.Equals(claim.Browser, StringComparison.OrdinalIgnoreCase)
                && match.Mobile == claim.Mobile
                && (claim.Version - match.Version) >= VersionDeltaMinDelta)
            {
                state.WriteSignal(SignalKeys.TlsVersionDeltaFromUa, claim.Version - match.Version);
                contributions.Add(BotContribution(
                    "TLS",
                    $"TLS version delta: UA claims {claim.Browser} {claim.Version} but TLS matches {claim.Browser} {match.Version}",
                    confidenceOverride: VersionDeltaConfidence,
                    weightMultiplier: VersionDeltaWeight,
                    botType: BotType.Scraper.ToString()));
            }
        }
    }

    private sealed record UaBrowserClaim(string Browser, int Version, bool Mobile);

    private static UaBrowserClaim? ParseUaBrowserClaim(string userAgent)
    {
        // Order matters: Edge identifies as both Edg and Chrome; check it first.
        // Same for Opera (OPR). Returns null for UAs we don't recognise; the
        // corpus checks gracefully skip those.
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
        // Safari version lives in the Version/ token before "Safari/", not in Safari/.
        var safari = SafariVersionRegex().Match(userAgent);
        if (safari.Success && userAgent.Contains("Safari", StringComparison.Ordinal)
            && !userAgent.Contains("Chrome", StringComparison.Ordinal)
            && int.TryParse(safari.Groups[1].Value, out var safariVer))
            return new UaBrowserClaim("safari", safariVer, mobile);
        return null;
    }

    /// <summary>
    ///     Decides whether <paramref name="observed"/> is a strict cipher-list
    ///     subset of <paramref name="reference"/>: the TLS version (part 0),
    ///     extensions (part 2), curves (part 3), and formats (part 4) all match
    ///     exactly, AND the observed cipher list (part 1) is a strict subset
    ///     of the reference cipher list with 1..<see cref="CipherSubsetMaxMissing"/>
    ///     entries missing. Returns the missing count in <paramref name="missingCount"/>.
    /// </summary>
    private bool IsStrictCipherSubset(string observed, string reference, out int missingCount)
    {
        missingCount = 0;
        var oParts = observed.Split(',');
        var rParts = reference.Split(',');
        if (oParts.Length != 5 || rParts.Length != 5) return false;
        // Non-cipher parts must match exactly (no GREASE / extension drift).
        if (oParts[0] != rParts[0] || oParts[2] != rParts[2]
            || oParts[3] != rParts[3] || oParts[4] != rParts[4]) return false;

        var oSet = oParts[1].Split('-').ToHashSet();
        var rSet = rParts[1].Split('-').ToHashSet();
        // Strict subset: observed ciphers all appear in reference, AND reference
        // has at least one cipher the observed doesn't.
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