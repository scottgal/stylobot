namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Deterministic bot name synthesizer that generates meaningful names from detection signals
///     without requiring an LLM. Provides immediate naming on first detection, before the
///     LLM description service has had time to process. LLM-generated names override these
///     when available.
///
///     Name pattern: "{Behavior} {Tool/Family}" e.g. "Rapid Scraper", "Stealthy Crawler",
///     "Rotating Python Bot", "Credential Stuffer"
/// </summary>
public sealed class DeterministicBotNameSynthesizer : IBotNameSynthesizer
{
    public bool IsReady => true;

    public Task<string?> SynthesizeBotNameAsync(
        IReadOnlyDictionary<string, object?> signals,
        CancellationToken ct = default)
    {
        return Task.FromResult<string?>(GenerateName(signals));
    }

    public Task<(string? Name, string? Description)> SynthesizeDetailedAsync(
        IReadOnlyDictionary<string, object?> signals,
        string? context = null,
        CancellationToken ct = default)
    {
        var name = GenerateName(signals);
        var desc = GenerateDescription(signals);
        return Task.FromResult<(string?, string?)>((name, desc));
    }

    private static string GenerateName(IReadOnlyDictionary<string, object?> signals)
    {
        var family = GetString(signals, "ua.family");
        var botName = GetString(signals, "ua.bot_name");
        var botType = GetString(signals, "ua.bot_type");
        var intent = GetString(signals, "intent.category");
        var threatBand = GetString(signals, "intent.threat_band");
        var signature = GetString(signals, "signature.primary");
        var country = GetString(signals, "geo.country_code");

        // Known bot name from UA parsing -- highest confidence signal
        if (!string.IsNullOrEmpty(botName) && botName != "unknown")
            return Unique(botName, signature, country);

        // Build from highest-signal evidence
        var behavior = GetBehaviorAdjective(signals);
        var tool = GetToolNoun(family, botType, intent);

        if (!string.IsNullOrEmpty(behavior) && !string.IsNullOrEmpty(tool))
            return Unique($"{behavior} {tool}", signature, country);

        if (!string.IsNullOrEmpty(tool))
            return Unique(tool, signature, country);

        if (!string.IsNullOrEmpty(threatBand) && threatBand != "None")
            return Unique($"{threatBand}-threat {botType ?? "client"}", signature, country);

        if (!string.IsNullOrEmpty(botType))
            return Unique(botType, signature, country);

        // Temporary label from whatever we have -- the LLM will replace this
        // with a proper centroid-derived name once it processes the signature
        if (!string.IsNullOrEmpty(family))
            return Unique(family, signature, country);

        return Unique("analysing", signature, country);
    }

    /// <summary>
    ///     Make every name unique using signature hash + geography.
    ///     "curl" becomes "curl (DE:a8f2)" -- unique, recognizable, geographically contextual.
    ///     The LLM will later replace this with a centroid-derived descriptive name.
    /// </summary>
    private static string Unique(string baseName, string? signature, string? country)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(country)) parts.Add(country);
        if (!string.IsNullOrEmpty(signature) && signature.Length >= 4) parts.Add(signature[..4]);

        return parts.Count > 0 ? $"{baseName} ({string.Join(":", parts)})" : baseName;
    }

    private static string GetBehaviorAdjective(IReadOnlyDictionary<string, object?> signals)
    {
        var burstRatio = GetDouble(signals, "waveform.burst_ratio");
        var pageRate = GetDouble(signals, "waveform.page_rate");
        var pathDiversity = GetDouble(signals, "waveform.path_diversity");
        var velocityMag = GetDouble(signals, "session.velocity_magnitude");
        var assetRatio = GetDouble(signals, "waveform.asset_ratio");

        // High velocity between sessions = rotating/shifting
        if (velocityMag > 0.5) return "Rotating";

        // Very high request rate = rapid
        if (pageRate > 20) return "Rapid";

        // No assets loaded = headless/automated
        if (assetRatio < 0.01 && pageRate > 2) return "Headless";

        // Low path diversity + high rate = targeted
        if (pathDiversity < 0.2 && pageRate > 5) return "Targeted";

        // High burst ratio = bursty
        if (burstRatio > 0.5) return "Bursty";

        // High path diversity = exploratory
        if (pathDiversity > 0.8) return "Exploratory";

        return "Automated";
    }

    private static string GetToolNoun(string? family, string? botType, string? intent)
    {
        // Map intent to noun
        var intentNoun = intent?.ToLowerInvariant() switch
        {
            "scanning" => "Scanner",
            "scraping" => "Scraper",
            "exploitation" or "attacking" => "Attacker",
            "reconnaissance" => "Prober",
            "monitoring" => "Monitor",
            "abuse" => "Abuser",
            "browsing" => null, // Don't name browsers
            _ => null
        };

        // Map bot type to noun
        var typeNoun = botType?.ToLowerInvariant() switch
        {
            "tool" => "Tool",
            "scraper" => "Scraper",
            "crawler" => "Crawler",
            "searchengine" => "Search Crawler",
            "socialmediabot" => "Social Bot",
            "monitoringbot" => "Monitor",
            "maliciousbot" => "Bot",
            "aibot" => "AI Crawler",
            _ => null
        };

        // Map UA family to noun
        var familyNoun = family?.ToLowerInvariant() switch
        {
            "curl" => "cURL Client",
            "python-requests" or "python" => "Python Bot",
            "go-http-client" or "go" => "Go Client",
            "java" => "Java Client",
            "axios" or "node-fetch" or "node" => "Node.js Bot",
            "scrapy" => "Scrapy Crawler",
            "httpclient" => "HTTP Client",
            "wget" => "Wget Client",
            "libwww-perl" => "Perl Bot",
            "ruby" => "Ruby Client",
            "php" => "PHP Client",
            _ => null
        };

        // Priority: intent > family > type
        return intentNoun ?? familyNoun ?? typeNoun ?? "Bot";
    }

    private static string? GenerateDescription(IReadOnlyDictionary<string, object?> signals)
    {
        var parts = new List<string>();

        var family = GetString(signals, "ua.family");
        if (!string.IsNullOrEmpty(family))
            parts.Add($"User-Agent family: {family}");

        var intent = GetString(signals, "intent.category");
        if (!string.IsNullOrEmpty(intent))
            parts.Add($"Intent: {intent}");

        var pageRate = GetDouble(signals, "waveform.page_rate");
        if (pageRate > 0)
            parts.Add($"Request rate: {pageRate:F1} pages/min");

        var assetRatio = GetDouble(signals, "waveform.asset_ratio");
        if (assetRatio < 0.01)
            parts.Add("No static assets loaded (headless behavior)");

        return parts.Count > 0 ? string.Join(". ", parts) + "." : null;
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> signals, string key)
        => signals.TryGetValue(key, out var v) && v is string s && !string.IsNullOrEmpty(s) ? s : null;

    private static double GetDouble(IReadOnlyDictionary<string, object?> signals, string key)
        => signals.TryGetValue(key, out var v) && v is double d ? d : 0;
}
