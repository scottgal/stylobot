using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Render-layer helpers for turning a <see cref="DashboardTopBotEntry"/> (or its
///     <see cref="DashboardVisitorEntry"/> sibling) into a human-readable display
///     label. Previously these lived in <c>@functions</c> blocks at the bottom of
///     <c>SbTopBots/Default.cshtml</c> and <c>_RecentActivity.cshtml</c>, where
///     <c>DeterministicIntent</c> and <c>ResolvedThreat</c> were duplicated verbatim
///     and the 100+ lines of pattern-matching against detection reason strings sat
///     in Razor where they could not be unit-tested.
///     <para>
///     The reason-prefix matches here are stringly-coupled to the freeform strings
///     produced by <c>ContributingDetectors/*Contributor.cs</c>. When the detection
///     layer renames a reason ("Severe login brute-forcing" -> "Credential brute-force"
///     etc.) the matching activity label here silently breaks. The proper fix is
///     to surface a stable category code on <c>DetectionContribution</c> and switch
///     on that, but until then keeping the heuristic here -- testable, single-source --
///     is the lesser evil over scattered <c>@functions</c> blocks.
///     </para>
/// </summary>
public static class BotDisplayHelpers
{
    /// <summary>
    ///     Map a 2-letter ISO country code to its English demonym.
    ///     Falls through to the raw code for any country not in the small set we display.
    /// </summary>
    public static string CountryAdjective(string code) => code.ToUpperInvariant() switch
    {
        "CN" => "Chinese",
        "RU" => "Russian",
        "US" => "US",
        "DE" => "German",
        "FR" => "French",
        "IN" => "Indian",
        "BR" => "Brazilian",
        "KR" => "Korean",
        "UA" => "Ukrainian",
        "NL" => "Dutch",
        "GB" => "British",
        "JP" => "Japanese",
        "CA" => "Canadian",
        "AU" => "Australian",
        "SG" => "Singapore",
        "HK" => "HK",
        "TR" => "Turkish",
        "PL" => "Polish",
        "IT" => "Italian",
        "ES" => "Spanish",
        "CZ" => "Czech",
        "RO" => "Romanian",
        "ID" => "Indonesian",
        "VN" => "Vietnamese",
        "IR" => "Iranian",
        "PK" => "Pakistani",
        _ => code
    };

    /// <summary>
    ///     Classify a requested path into a scanner type (ENV / Config / Admin / Backup /
    ///     API / Auth / App / generic Path) based on the actual file/path it was looking for.
    /// </summary>
    public static string CategorizePath(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.Contains(".env") || p.Contains("env.")) return "ENV Scanner";
        if (p.Contains("wp-config") || p.Contains("config.php") || p.Contains("config.yml") ||
            p.Contains("config.yaml") || p.Contains("settings.py") || p.Contains("application.properties") ||
            p.Contains("web.config") || p.Contains(".config")) return "Config Scanner";
        if (p.Contains("wp-admin") || p.Contains("/admin") || p.Contains("phpmyadmin") ||
            p.Contains("adminer") || p.Contains("/manage/")) return "Admin Scanner";
        if (p.Contains(".git") || p.Contains(".svn") || p.Contains("backup") ||
            p.Contains(".sql") || p.Contains(".bak") || p.Contains(".tar")) return "Backup Scanner";
        if (p.Contains("/api/") || p.Contains("/graphql") || p.Contains("/v1/") ||
            p.Contains("/v2/") || p.Contains("/v3/")) return "API Scanner";
        if (p.Contains("login") || p.Contains("signin") || p.Contains("/auth") ||
            p.Contains("wp-login") || p.Contains("xmlrpc")) return "Auth Scanner";
        if (p.Contains(".php") || p.Contains(".asp") || p.Contains(".jsp")) return "App Scanner";
        return "Path Scanner";
    }

    /// <summary>
    ///     Build a name from what the bot actually did.
    ///     Priority: threat-classified behaviour -> path-derived scan target ->
    ///     behavioural signals from reasons -> generic risk-band fallback.
    ///     Country and rate/origin modifiers are prepended where available.
    /// </summary>
    public static string DescriptiveBotName(DashboardTopBotEntry bot)
    {
        var reasons = bot.TopReasons ?? [];
        var country = (!string.IsNullOrEmpty(bot.CountryCode) && bot.CountryCode.Length == 2 && bot.CountryCode != "XX")
            ? CountryAdjective(bot.CountryCode)
            : "";

        var modifier = "";
        var activity = "";

        foreach (var r in reasons)
        {
            if (r.StartsWith("Severe login brute-forcing:") || r.StartsWith("Repeated login failures:"))
            { activity = "Credential Attack"; break; }
            if (r.StartsWith("Client accessed") && r.Contains("honeypot"))
            { activity = "Threat Probe"; break; }
            if (r.StartsWith("Error harvesting detected:") || r.StartsWith("Triggering errors:"))
            { activity = "Error Harvester"; break; }
        }

        if (string.IsNullOrEmpty(activity) && !string.IsNullOrEmpty(bot.LastPath))
            activity = CategorizePath(bot.LastPath);

        if (string.IsNullOrEmpty(activity))
        {
            foreach (var r in reasons)
            {
                if (r.StartsWith("Only accessing data endpoints")) { activity = "API Scanner"; break; }
                if (r.StartsWith("Systematic scanning detected:") || r.StartsWith("Exclusive 404 pattern:")) { activity = "Path Scanner"; break; }
                if (r.StartsWith("Probable scanning:") || r.StartsWith("Multiple 404s on distinct paths:")) { activity = "Path Scanner"; break; }
                if (r.StartsWith("Strict depth-first traversal")) { activity = "Web Crawler"; break; }
                if (r.StartsWith("No mouse movement detected")) { activity = "Headless Browser"; break; }
                if (r.StartsWith("IP classified as search engine by Project Honeypot")) { activity = "Known Threat"; break; }
                if (r.StartsWith("Some login failures:")) { activity = "Auth Probe"; break; }
            }
        }

        foreach (var r in reasons)
        {
            if (r.StartsWith("Burst detected:")) { modifier = "Rapid"; break; }
            if (r.StartsWith("Elevated request rate:")) { modifier = "Rapid"; break; }
            if (r.StartsWith("Multiple rate limit violations:") || r.StartsWith("Exceeded request speed limits")) { modifier = "Rapid"; break; }
            if (r.StartsWith("Datacenter IP detected:")) { if (string.IsNullOrEmpty(modifier)) modifier = "Cloud"; break; }
            if (r.StartsWith("Behind reverse proxy:")) { if (string.IsNullOrEmpty(modifier)) modifier = "Proxied"; break; }
        }

        if (string.IsNullOrEmpty(activity))
        {
            // Low-prob entities (< 0.5) are humans -- calling a 1% bot-probability visitor a
            // "Suspicious Client" was a glaring bug. High-prob with no specific signal is
            // genuinely an unidentified automated client; medium is the ambiguous middle.
            activity = (bot.RiskBand ?? bot.ThreatBand) switch
            {
                "VeryHigh" => "High-Risk Scanner",
                "High" => "Automated Scanner",
                "Medium" or "Elevated" => "Automated Client",
                _ => bot.BotProbability >= 0.90 ? "Automated Client"
                   : bot.BotProbability >= 0.50 ? "Suspicious Client"
                   : "Visitor"
            };
        }

        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(country)) parts.Add(country);
        if (!string.IsNullOrEmpty(modifier)) parts.Add(modifier);
        parts.Add(activity);
        return string.Join(" ", parts);
    }

    /// <summary>
    ///     Probability -> coarse intent label. Used as a deterministic fallback when
    ///     the upstream <c>BotType</c> field is missing.
    /// </summary>
    public static string DeterministicIntent(double prob) => prob switch
    {
        >= 0.95 => "Automated",
        >= 0.80 => "Scanner",
        >= 0.60 => "Crawler",
        _ => "Probe"
    };

    /// <summary>
    ///     Resolve a threat band string: honour the upstream value when present and non-"None",
    ///     otherwise derive from bot probability. Non-bots get an empty band -- showing a
    ///     "Low" threat on a verified human is misleading.
    /// </summary>
    public static string ResolvedThreat(string? band, double prob, bool isBot = true)
    {
        if (!string.IsNullOrEmpty(band) && band != "None") return band;
        if (!isBot) return "";
        return prob switch
        {
            >= 0.95 => "High",
            >= 0.75 => "Medium",
            _ => "Low"
        };
    }
}
