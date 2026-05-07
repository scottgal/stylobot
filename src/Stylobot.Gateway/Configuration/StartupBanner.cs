namespace Stylobot.Gateway.Configuration;

public static class StartupBanner
{
    public static void Print(IConfiguration config, TlsOptions tls)
    {
        var version = typeof(StartupBanner).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var httpPort = config.GetValue("GATEWAY_HTTP_PORT", 8080);
        var upstream = Environment.GetEnvironmentVariable("DEFAULT_UPSTREAM");
        var yarpConfig = GatewayPaths.YarpConfig;
        var botThreshold = config.GetValue("BotDetection:BotThreshold", 0.7);
        var policy = config.GetValue("BotDetection:DefaultActionPolicyName", "throttle-stealth");
        var adminPath = config.GetValue("Gateway:AdminBasePath", "/admin");
        var adminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET")
                          ?? config.GetValue<string>("Gateway:AdminSecret");

        var httpsLine = tls.Enabled
            ? $":{tls.Port}  ({(tls.IsAcme ? $"ACME / {tls.Domain}" : "cert-from-file")})"
            : "disabled";

        string routeLine;
        if (!string.IsNullOrWhiteSpace(upstream))
            routeLine = $"{upstream}  [catch-all]";
        else if (File.Exists(yarpConfig))
            routeLine = $"config file  ({yarpConfig})";
        else
            routeLine = "NOT CONFIGURED";

        var adminLine = string.IsNullOrWhiteSpace(adminSecret)
            ? $"{adminPath}  [no secret - disabled]"
            : $"{adminPath}  [protected]";

        const int width = 58;
        var border = new string('═', width - 2);

        Console.WriteLine($"╔{border}╗");
        Console.WriteLine(Pad($"  StyloBot Gateway  v{version}", width));
        Console.WriteLine($"╠{border}╣");
        Console.WriteLine(Pad($"  HTTP   :{httpPort}", width));
        Console.WriteLine(Pad($"  HTTPS  {httpsLine}", width));
        Console.WriteLine(Pad($"  Route  {routeLine}", width));
        Console.WriteLine(Pad($"  Policy  {policy}  |  threshold  {botThreshold:F2}", width));
        Console.WriteLine(Pad($"  Admin  {adminLine}", width));
        Console.WriteLine($"╚{border}╝");

        if (string.IsNullOrWhiteSpace(adminSecret))
            Console.WriteLine("[WARN] ADMIN_SECRET not set -- admin API is disabled until configured");

        if (routeLine == "NOT CONFIGURED")
            Console.WriteLine("[WARN] No proxy routes -- gateway returns 503 for all requests; set DEFAULT_UPSTREAM or mount a yarp.json");
    }

    private static string Pad(string content, int width)
    {
        var truncated = content.Length > width - 4 ? content[..(width - 7)] + "..." : content;
        return $"║{truncated.PadRight(width - 2)}║";
    }
}
