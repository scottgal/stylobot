using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;
using MatchType = Mostlylucid.BotDetection.Dashboard.MatchType;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Fast-path signature matcher for FIRST-HIT detection using multi-factor signatures.
///     ARCHITECTURE:
///     - Runs BEFORE expensive detectors (Wave 0, Priority 1)
///     - Extracts server-side signature factors ONLY (IP + UA)
///     - Client-side factors (Canvas, WebGL, etc.) come LATER via postback
///     - Uses weighted scoring to avoid false positives
///     - Guards against accidental matches (requires minimum confidence)
///     SIGNATURE FACTORS (Server-Side Only):
///     1. Primary: HMAC(IP + UA) - exact match required (100% confidence)
///     2. IP: HMAC(IP) - handles UA changes (50% weight)
///     3. UA: HMAC(UA) - handles IP changes (50% weight)
///     4. IP Subnet: HMAC(IP /24) - same network (30% weight)
///     CLIENT-SIDE FACTORS (Postback Only):
///     - ClientSide: HMAC(Canvas+WebGL+AudioContext) - browser fingerprint (80% weight)
///     - Plugin: HMAC(Plugins+Extensions+Fonts) - browser config (60% weight)
///     WEIGHTED SCORING (Guards Against False Positives):
///     - Require PRIMARY match (100%) OR
///     - Require 2+ factors with min combined weight of 100% OR
///     - Require 3+ factors with min combined weight of 80%
///     EXAMPLE FALSE POSITIVE PREVENTION:
///     - Same IP + Same UA BUT different users in office → NO MATCH
///     (Primary differs due to subtle UA variations, need client-side to confirm)
///     - IP match only (weight 50%) → NO MATCH (too low confidence)
///     - UA match only (weight 50%) → NO MATCH (too low confidence)
///     - IP + UA match (weight 100%) → MATCH (equivalent to Primary)
///     - IP + Subnet match (weight 80%) → NO MATCH (below threshold)
/// </summary>
public sealed class FastPathSignatureMatcher
{
    private readonly ILogger<FastPathSignatureMatcher> _logger;
    private readonly SignatureMatchingOptions _options;
    private readonly Func<string, CancellationToken, Task<StoredSignature?>>? _signatureLookup;
    private readonly MultiFactorSignatureService _signatureService;

    public FastPathSignatureMatcher(
        MultiFactorSignatureService signatureService,
        ILogger<FastPathSignatureMatcher> logger,
        BotDetectionOptions botOptions,
        Func<string, CancellationToken, Task<StoredSignature?>>? signatureLookup = null)
    {
        _signatureService = signatureService;
        _logger = logger;
        _signatureLookup = signatureLookup;
        _options = botOptions.FastPath.SignatureMatching;
    }

    /// <summary>
    ///     Attempts fast-path signature matching using server-side factors only.
    ///     Returns match result with weighted confidence score.
    ///     CRITICAL: This runs BEFORE client-side data is available!
    ///     Client-side factors (Canvas, WebGL) are updated via postback AFTER response.
    /// </summary>
    public async Task<SignatureMatchResult?> TryMatchAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Generate server-side signature factors
            var currentSignatures = _signatureService.GenerateSignatures(httpContext);

            // Quick check: Do we have enough factors to even try matching?
            if (currentSignatures.FactorCount < 2)
            {
                _logger.LogDebug("Insufficient signature factors ({Count}) for fast-path matching",
                    currentSignatures.FactorCount);
                return null;
            }

            // Try to find matching signature (requires SignatureStore integration)
            if (_signatureLookup == null)
            {
                _logger.LogDebug("SignatureLookup not configured - fast-path matching disabled");
                return null;
            }

            var storedSignature = await _signatureLookup(
                currentSignatures.PrimarySignature,
                cancellationToken);

            if (storedSignature == null)
            {
                _logger.LogDebug("No stored signature found for {SignatureId}",
                    currentSignatures.PrimarySignature.Substring(0,
                        Math.Min(12, currentSignatures.PrimarySignature.Length)));
                return null;
            }

            // Parse stored multi-factor signatures
            var storedSignatures = ParseStoredSignatures(storedSignature.Signatures);
            if (storedSignatures == null)
            {
                _logger.LogWarning("Failed to parse stored signatures for {SignatureId}", storedSignature.SignatureId);
                return null;
            }

            // Perform weighted multi-factor matching
            var matchResult = PerformWeightedMatching(currentSignatures, storedSignatures);

            // Guard against false positives - require minimum confidence
            if (!matchResult.IsMatch)
            {
                _logger.LogDebug(
                    "Signature match rejected - insufficient confidence: {Confidence:F1}% (need {MinConfidence:F1}%)",
                    matchResult.Confidence * 100, _options.MinWeightForMatch);
                return null;
            }

            _logger.LogInformation(
                "Fast-path signature MATCH: SignatureId={SignatureId}, Factors={Factors}, Confidence={Confidence:F1}%, MatchType={MatchType}",
                storedSignature.SignatureId.Substring(0, Math.Min(12, storedSignature.SignatureId.Length)),
                string.Join("+", matchResult.MatchedFactors),
                matchResult.Confidence * 100,
                matchResult.MatchType);

            return matchResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fast-path signature matching failed");
            return null;
        }
    }

    /// <summary>
    ///     Performs weighted multi-factor matching with false positive prevention.
    ///     RULES (Priority Order):
    ///     1. Primary match → 100% confidence (exact same IP+UA)
    ///     2. IP + UA match → 100% confidence (equivalent to primary)
    ///     3. 2 factors with combined weight ≥100% → MATCH
    ///     4. 3+ factors with combined weight ≥80% → WEAK MATCH
    ///     5. Otherwise → NO MATCH (avoid false positives)
    /// </summary>
    private SignatureMatchResult PerformWeightedMatching(
        MultiFactorSignatures current,
        MultiFactorSignatures stored)
    {
        var matchedFactors = new List<string>();
        var totalWeight = 0.0;

        // Check Primary (IP+UA composite)
        if (current.PrimarySignature == stored.PrimarySignature)
        {
            matchedFactors.Add("Primary");
            totalWeight += _options.WeightPrimary;

            // Primary match is instant 100% confidence
            return new SignatureMatchResult
            {
                IsMatch = true,
                Confidence = 1.0,
                MatchedFactors = matchedFactors,
                MatchType = MatchType.Exact,
                FactorsMatched = 1,
                TotalFactors = current.FactorCount,
                TotalWeight = totalWeight,
                Explanation = "Exact primary signature match (IP+UA identical)"
            };
        }

        // Check IP (handles UA changes - browser updates)
        if (current.IpSignature != null && current.IpSignature == stored.IpSignature)
        {
            matchedFactors.Add("IP");
            totalWeight += _options.WeightIp;
        }

        // Check UA (handles IP changes - dynamic ISPs, mobile networks)
        if (current.UaSignature != null && current.UaSignature == stored.UaSignature)
        {
            matchedFactors.Add("UA");
            totalWeight += _options.WeightUa;
        }

        // Check IP Subnet (network-level grouping)
        if (current.IpSubnetSignature != null && current.IpSubnetSignature == stored.IpSubnetSignature)
        {
            matchedFactors.Add("IpSubnet");
            totalWeight += _options.WeightIpSubnet;
        }

        // Decision: Do we have enough confidence for a match?
        var isMatch = false;
        var matchType = MatchType.Weak;
        var explanation = "";
        var confidence = totalWeight / 100.0; // Normalize to 0-1

        // Rule 1: IP + UA match (equivalent to Primary)
        if (matchedFactors.Contains("IP") && matchedFactors.Contains("UA"))
        {
            isMatch = true;
            matchType = MatchType.Exact;
            confidence = 1.0;
            explanation = "IP and UA both match (equivalent to primary signature)";
        }
        // Rule 2: 2 factors with combined weight ≥MinWeightForMatch
        else if (matchedFactors.Count >= 2 && totalWeight >= _options.MinWeightForMatch)
        {
            isMatch = true;
            matchType = MatchType.Partial;
            explanation = $"{matchedFactors.Count} factors matched with {totalWeight:F0}% confidence";
        }
        // Rule 3: MinFactorsForWeakMatch+ factors with combined weight ≥MinWeightForWeakMatch (weak match)
        else if (matchedFactors.Count >= _options.MinFactorsForWeakMatch &&
                 totalWeight >= _options.MinWeightForWeakMatch)
        {
            isMatch = true;
            matchType = MatchType.Partial;
            confidence = totalWeight / 100.0;
            explanation = $"{matchedFactors.Count} factors matched with {totalWeight:F0}% confidence (weak match)";
        }
        // Rule 4: Not enough confidence - reject to avoid false positives
        else
        {
            isMatch = false;
            matchType = MatchType.Weak;
            explanation = matchedFactors.Count > 0
                ? $"Only {matchedFactors.Count} factor(s) matched with {totalWeight:F0}% confidence (insufficient, need {_options.MinWeightForMatch}%)"
                : "No matching factors found";
        }

        return new SignatureMatchResult
        {
            IsMatch = isMatch,
            Confidence = confidence,
            MatchedFactors = matchedFactors,
            MatchType = matchType,
            FactorsMatched = matchedFactors.Count,
            TotalFactors = current.FactorCount,
            TotalWeight = totalWeight,
            Explanation = explanation
        };
    }

    /// <summary>
    ///     Parses stored signatures from JSONB column.
    ///     Format: {"primary": "hash", "ip": "hash", "ua": "hash", ...}
    /// </summary>
    private MultiFactorSignatures? ParseStoredSignatures(string? signaturesJson)
    {
        if (string.IsNullOrEmpty(signaturesJson))
            return null;

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(signaturesJson);
            if (dict == null)
                return null;

            return new MultiFactorSignatures
            {
                PrimarySignature = dict.GetValueOrDefault("primary") ?? "",
                IpSignature = dict.GetValueOrDefault("ip"),
                UaSignature = dict.GetValueOrDefault("ua"),
                ClientSideSignature = dict.GetValueOrDefault("clientSide"),
                PluginSignature = dict.GetValueOrDefault("plugin"),
                IpSubnetSignature = dict.GetValueOrDefault("ipSubnet"),
                IpUaSignature = dict.GetValueOrDefault("ipUa"),
                IpClientSignature = dict.GetValueOrDefault("ipClient"),
                UaClientSignature = dict.GetValueOrDefault("uaClient"),
                Timestamp = DateTime.UtcNow,
                FactorCount = dict.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse stored signatures JSON: {Json}",
                signaturesJson.Substring(0, Math.Min(100, signaturesJson.Length)));
            return null;
        }
    }
}

/// <summary>
///     Result of signature matching with weighted confidence scoring.
/// </summary>
public sealed class SignatureMatchResult
{
    /// <summary>Whether signatures match with sufficient confidence</summary>
    public bool IsMatch { get; init; }

    /// <summary>Match confidence (0.0-1.0) based on weighted scoring</summary>
    public double Confidence { get; init; }

    /// <summary>Which factors matched</summary>
    public List<string> MatchedFactors { get; init; } = new();

    /// <summary>Type of match (Exact, Partial, Weak)</summary>
    public MatchType MatchType { get; init; }

    /// <summary>Number of factors that matched</summary>
    public int FactorsMatched { get; init; }

    /// <summary>Total number of factors available</summary>
    public int TotalFactors { get; init; }

    /// <summary>Total combined weight of matched factors</summary>
    public double TotalWeight { get; init; }

    /// <summary>Plain English explanation of the match result</summary>
    public string Explanation { get; init; } = "";
}

/// <summary>
///     Simple DTO for stored signature data.
///     Avoids dependency on SignatureStore project.
/// </summary>
public sealed class StoredSignature
{
    public required string SignatureId { get; init; }
    public string? Signatures { get; init; }
    public double BotProbability { get; init; }
    public DateTime Timestamp { get; init; }
}