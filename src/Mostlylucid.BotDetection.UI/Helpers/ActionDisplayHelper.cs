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
        "block" => "Block",
        "block-hard" => "Hard Block",
        "block-soft" => "Soft Block",
        "logonly" => "Monitor Only",
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

    public static string GetCssClass(string? action) => action switch
    {
        "Block" or "block" or "block-hard" or "block-soft" => "text-error",
        "Throttle" or "throttle" or "throttle-stealth" or "throttle-gentle"
            or "throttle-moderate" or "throttle-aggressive" => "text-warning",
        "Challenge" or "challenge" or "challenge-captcha"
            or "challenge-pow" or "challenge-js" => "text-info",
        "TarPit" or "redirect-tarpit" or "simulation-pack" => "text-error",
        _ => "text-base-content/50"
    };

    public static string GetBadgeCssClass(string? action) => action switch
    {
        "Block" or "block" or "block-hard" or "block-soft" => "bg-error/20 text-error",
        "Throttle" or "throttle" or "throttle-stealth" or "throttle-gentle"
            or "throttle-moderate" or "throttle-aggressive" => "bg-warning/20 text-warning",
        "Challenge" or "challenge" or "challenge-captcha"
            or "challenge-pow" or "challenge-js" => "bg-info/20 text-info",
        "TarPit" or "redirect-tarpit" or "simulation-pack" => "bg-error/20 text-error",
        _ => ""
    };
}
