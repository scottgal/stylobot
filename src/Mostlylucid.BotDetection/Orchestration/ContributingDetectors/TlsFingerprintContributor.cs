using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
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
public class TlsFingerprintContributor : ConfiguredContributorBase
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

    public TlsFingerprintContributor(
        ILogger<TlsFingerprintContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
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

            // Get TLS protocol from reverse proxy header (e.g., nginx: $ssl_protocol)
            if (state.HttpContext.Request.Headers.TryGetValue("X-TLS-Protocol", out var tlsProtoHeader))
            {
                var protocol = tlsProtoHeader.ToString();
                state.WriteSignal(SignalKeys.TlsProtocol, protocol);
                AnalyzeTlsProtocol(protocol, contributions, state);
            }

            // Get cipher suite from reverse proxy header (e.g., nginx: $ssl_cipher)
            if (state.HttpContext.Request.Headers.TryGetValue("X-TLS-Cipher", out var cipherHeader))
            {
                var cipher = cipherHeader.ToString();
                state.WriteSignal("tls.cipher_suite", cipher);
                AnalyzeCipherSuite(cipher, contributions, state);
            }

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
}