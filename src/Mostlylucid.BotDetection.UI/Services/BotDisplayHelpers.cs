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
    ///     Resolve a threat band string. Honours the upstream value when present
    ///     and non-"None"; otherwise returns "None" (NOT a probability-derived
    ///     guess). A 100% bot row paired with "High threat" derived from
    ///     probability misreads as a contradiction on verified-good bots like
    ///     Googlebot -- bot probability says "automated", threat says
    ///     "dangerous", and the two are orthogonal axes.
    /// </summary>
    public static string ResolvedThreat(string? band, double prob, bool isBot = true)
    {
        if (!string.IsNullOrEmpty(band) && band != "None") return band;
        return isBot ? "None" : "";
    }

    /// <summary>
    ///     Categorical "is this dangerous" label for bot rows. Separate from
    ///     bot probability ("is this automated"). Returns "Benign" /
    ///     "Suspicious" / "Hostile" so the dashboard never paints
    ///     "100% Bot · THREAT 0.0" as a contradiction -- the row reads
    ///     "100% Bot · Benign" for a verified Googlebot,
    ///     "100% Bot · Hostile" for an /.aws/credentials probe.
    /// </summary>
    public static string IntentLabel(string? threatBand, bool isBot)
    {
        if (!isBot) return "";
        return threatBand switch
        {
            "Critical" or "High" => "Hostile",
            "Elevated" or "Medium" => "Suspicious",
            _ => "Benign"
        };
    }

    /// <summary>Tailwind text-colour for the Benign/Suspicious/Hostile label.</summary>
    public static string ColorForIntent(string label) => label switch
    {
        "Hostile" => "text-error",
        "Suspicious" => "text-warning",
        "Benign" => "text-info",
        _ => "text-base-content/40"
    };

    // ─── Icon / colour helpers for the Cloudflare-style consolidated row ─────────
    // Goal: replace text-heavy labels with iconography + colour so a row reads at a
    // glance. The mapping is intentionally single-source -- every dashboard surface
    // that wants "icon for this bot type" should call into here so a future relabel
    // touches one switch.

    /// <summary>Boxicon class for a bot type, e.g. "bx bx-search-alt" for SearchEngine.</summary>
    public static string IconForBotType(string? botType) => botType switch
    {
        "SearchEngine"   => "bx bx-search-alt",
        "VerifiedBot"    => "bx bx-shield-quarter",
        "GoodBot"        => "bx bx-bot",
        "SocialMediaBot" => "bx bx-link-alt",
        "MonitoringBot"  => "bx bx-pulse",
        "AiBot"          => "bx bx-brain",
        "Tool"           => "bx bx-terminal",
        "Scraper"        => "bx bx-cloud-download",
        "MaliciousBot"   => "bx bx-shield-x",
        "ExploitScanner" => "bx bx-bug",
        "ClickFraud"     => "bx bx-pointer",
        _                => "bx bx-user"
    };

    /// <summary>Tailwind text-colour class for a risk band.</summary>
    public static string ColorForRiskBand(string? band) => band switch
    {
        "VeryHigh" or "Critical" => "text-error",
        "High"                   => "text-error",
        "Elevated" or "Medium"   => "text-warning",
        "Low"                    => "text-success",
        "VeryLow"                => "text-base-content/60",
        _                        => "text-base-content/40"
    };

    /// <summary>Tailwind background tint for a category badge.</summary>
    public static string CategoryBadgeClass(string? botType) => botType switch
    {
        "SearchEngine" or "VerifiedBot" or "GoodBot" => "bg-info/15 text-info",
        "AiBot"                                      => "bg-secondary/15 text-secondary",
        "Scraper" or "MaliciousBot" or "ExploitScanner" => "bg-error/15 text-error",
        "Tool"                                       => "bg-warning/15 text-warning",
        "MonitoringBot"                              => "bg-success/15 text-success",
        "SocialMediaBot"                             => "bg-accent/15 text-accent",
        _                                            => "bg-base-content/10 text-base-content/70"
    };

    /// <summary>Boxicon class for a threat band (orthogonal to risk: threat is "is this attacking").</summary>
    public static string IconForThreatBand(string? band) => band switch
    {
        "Critical" => "bx bxs-skull",
        "High"     => "bx bxs-error",
        "Elevated" => "bx bx-error-circle",
        "Low"      => "bx bx-info-circle",
        _          => "bx bx-shield" // None / null
    };

    /// <summary>Tailwind text-colour for a threat band.</summary>
    public static string ColorForThreatBand(string? band) => band switch
    {
        "Critical" => "text-error",
        "High"     => "text-error",
        "Elevated" => "text-warning",
        "Low"      => "text-info",
        _          => "text-base-content/30"
    };

    /// <summary>Boxicon for an action taken by the gateway.</summary>
    public static string IconForAction(string? action) => action switch
    {
        "Block"          => "bx bx-block",
        "Challenge"      => "bx bx-shield-quarter",
        "Throttle"       => "bx bx-time-five",
        "ThrottleStealth"=> "bx bx-time",
        "Redirect"       => "bx bx-rotate-right",
        "LogOnly"        => "bx bx-show",
        _                => "bx bx-check"
    };

    /// <summary>Tailwind text-colour for an action.</summary>
    public static string ColorForAction(string? action) => action switch
    {
        "Block" or "Challenge"          => "text-error",
        "Throttle" or "ThrottleStealth" => "text-warning",
        "Redirect"                      => "text-info",
        "LogOnly"                       => "text-base-content/50",
        _                               => "text-success"
    };

    /// <summary>
    ///     Boxicon class for an HTTP method. Used alongside <see cref="ColorForHttpMethod"/>
    ///     to render the method as a coloured icon-pill in endpoint cards.
    /// </summary>
    public static string IconForHttpMethod(string? method) => (method ?? "").ToUpperInvariant() switch
    {
        "GET"     => "bx bx-down-arrow-alt",
        "POST"    => "bx bx-up-arrow-alt",
        "PUT"     => "bx bx-edit",
        "PATCH"   => "bx bx-edit-alt",
        "DELETE"  => "bx bx-trash",
        "HEAD"    => "bx bx-info-circle",
        "OPTIONS" => "bx bx-cog",
        _         => "bx bx-transfer"
    };

    /// <summary>Tailwind text-colour for an HTTP method (matches REST mutation severity).</summary>
    public static string ColorForHttpMethod(string? method) => (method ?? "").ToUpperInvariant() switch
    {
        "GET" or "HEAD" or "OPTIONS" => "text-success",
        "POST"                       => "text-warning",
        "PUT" or "PATCH"             => "text-info",
        "DELETE"                     => "text-error",
        _                            => "text-base-content/50"
    };

    /// <summary>
    ///     Boxicon for the path category returned by <see cref="CategorizePath"/>.
    ///     Kept beside CategorizePath so the icon and label stay in sync if we
    ///     add or rename categories.
    /// </summary>
    public static string IconForPathCategory(string category) => category switch
    {
        "ENV Scanner"     => "bx bx-key",
        "Config Scanner"  => "bx bx-cog",
        "Admin Scanner"   => "bx bx-shield-quarter",
        "Backup Scanner"  => "bx bx-archive",
        "API Scanner"     => "bx bx-server",
        "Auth Scanner"    => "bx bx-lock",
        "App Scanner"     => "bx bx-code-alt",
        _                 => "bx bx-link"
    };

    /// <summary>Severity ranking used to break ties and compare bands.</summary>
    public static int RiskSeverity(string? band) => band switch
    {
        "VeryHigh" or "Critical" => 5,
        "High"                   => 4,
        "Elevated" or "Medium"   => 3,
        "Low"                    => 2,
        "VeryLow"                => 1,
        _                        => 0
    };

    /// <summary>
    ///     Format a processing-time value (in fractional milliseconds, as written by
    ///     Stopwatch.Elapsed.TotalMilliseconds) with the appropriate unit so sub-ms
    ///     detections render with real resolution instead of "0ms". Detection paths
    ///     routinely complete in 100-900µs; rounding to integer ms collapsed the
    ///     entire distribution to 0/1 and made operator dashboards useless.
    ///
    ///     Output:
    ///     - &lt; 0.001 ms (under a µs) -> "&lt;1µs"
    ///     - &lt; 1 ms      -> integer microseconds, e.g. "437µs"
    ///     - &lt; 10 ms     -> 2-decimal ms, e.g. "1.23ms"
    ///     - &lt; 1000 ms   -> integer ms, e.g. "458ms"
    ///     - &lt; 60000 ms  -> 1-decimal seconds, e.g. "12.3s"
    ///     - otherwise   -> integer minutes, e.g. "3m"
    /// </summary>
    public static string FormatProcessingTime(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) return "--";
        if (ms < 0.001) return "<1µs";
        if (ms < 1.0)   return $"{ms * 1000.0:F0}µs";
        if (ms < 10.0)  return $"{ms:F2}ms";
        if (ms < 1000)  return $"{ms:F0}ms";
        if (ms < 60_000) return $"{ms / 1000.0:F1}s";
        return $"{ms / 60_000.0:F0}m";
    }

    /// <summary>Compact relative-time string ("12s", "5m", "3h 22m", "2d") for LastSeen.</summary>
    public static string TimeAgoCompact(DateTime when)
    {
        var span = DateTime.UtcNow - when;
        if (span.TotalSeconds < 5) return "now";
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d";
    }
}
