namespace Mostlylucid.BotDetection.UI.Helpers;

public static class ActionDisplayHelper
{
    public static string GetFriendlyName(string? action) => action switch
    {
        "throttle-stealth" => "Silent Throttle",
        "throttle" => "Throttle",
        "throttle-gentle" => "Gentle Throttle",
        "throttle-moderate" => "Moderate Throttle",
        "throttle-aggressive" => "Aggressive Throttle",
        "throttle-tools" => "Tool Throttle",
        "rate-limit-ai" => "AI Rate Limit",
        "rate-limit-search" => "Search Rate Limit",
        "rate-limit-social" => "Social Rate Limit",
        "rate-limit-monitoring" or "rate-limit-monitor" => "Monitoring Rate Limit",
        "block" => "Block",
        "block-hard" => "Hard Block",
        "block-soft" => "Soft Block",
        "logonly" => "Monitor Only",
        "allow" => "Allow",
        "challenge" => "Challenge",
        "challenge-captcha" => "CAPTCHA Challenge",
        "challenge-pow" => "PoW Challenge",
        "redirect-honeypot" => "Honeypot Redirect",
        "redirect-tarpit" => "Tarpit",
        "simulation-pack" => "Simulation Pack",
        "shadow" => "Shadow Mode",
        null or "" => "Allow",
        _ => action
    };

    // Color semantics (shared dashboard-wide): block=error, throttle/rate-limit=warning,
    // allow/logonly=success, challenge=info.
    public static string GetCssClass(string? action) => action switch
    {
        "Block" or "block" or "block-hard" or "block-soft" => "text-error",
        "Throttle" or "throttle" or "throttle-stealth" or "throttle-gentle"
            or "throttle-moderate" or "throttle-aggressive" or "throttle-tools"
            or "rate-limit-ai" or "rate-limit-search" or "rate-limit-social" or "rate-limit-monitoring" or "rate-limit-monitor"
            => "text-warning",
        "Challenge" or "challenge" or "challenge-captcha"
            or "challenge-pow" or "challenge-js" => "text-info",
        "TarPit" or "redirect-tarpit" or "simulation-pack" => "text-error",
        "Allow" or "allow" or "logonly" or null or "" => "text-success",
        _ => "text-base-content/50"
    };

    public static string GetBadgeCssClass(string? action) => action switch
    {
        "Block" or "block" or "block-hard" or "block-soft" => "bg-error/20 text-error",
        "Throttle" or "throttle" or "throttle-stealth" or "throttle-gentle"
            or "throttle-moderate" or "throttle-aggressive" or "throttle-tools"
            or "rate-limit-ai" or "rate-limit-search" or "rate-limit-social" or "rate-limit-monitoring" or "rate-limit-monitor"
            => "bg-warning/20 text-warning",
        "Challenge" or "challenge" or "challenge-captcha"
            or "challenge-pow" or "challenge-js" => "bg-info/20 text-info",
        "TarPit" or "redirect-tarpit" or "simulation-pack" => "bg-error/20 text-error",
        "Allow" or "allow" or "logonly" or null or "" => "bg-success/20 text-success",
        _ => ""
    };
}
