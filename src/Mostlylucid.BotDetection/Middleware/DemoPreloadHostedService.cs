using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Single-shot startup hook that drives one synthetic request per name in
///     <see cref="BotDetectionOptions.DemoPreloadOnStartup"/> through the
///     orchestrator. The detection's natural side-effects populate the
///     dashboard's signature aggregates, top-bots panel, and signal store from
///     request zero so demos and live talks aren't presented with an empty
///     dashboard.
/// </summary>
/// <remarks>
///     Not a recurring BackgroundService. <see cref="StartAsync"/> kicks off
///     the preload on a Task that runs once and exits. Failures per entry are
///     logged and swallowed; the host start is never blocked.
/// </remarks>
public sealed class DemoPreloadHostedService : IHostedService
{
    private static readonly Dictionary<string, string> BuiltInSimulations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["human"] =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            ["googlebot"] =
                "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            ["bingbot"] =
                "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",
            ["scrapy"] = "Scrapy/2.5.0 (+https://scrapy.org)",
            ["curl"] = "curl/8.4.0",
            ["malicious"] = "masscan/1.0 (https://github.com/robertdavidgraham/masscan)",
            ["mj12bot"] = "Mozilla/5.0 (compatible; MJ12bot/v1.4.8; http://mj12bot.com/)"
        };

    private readonly ILogger<DemoPreloadHostedService> _logger;
    private readonly IServiceProvider _services;
    private readonly BotDetectionOptions _options;

    public DemoPreloadHostedService(
        IServiceProvider services,
        IOptions<BotDetectionOptions> options,
        ILogger<DemoPreloadHostedService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableTestMode || _options.DemoPreloadOnStartup.Count == 0)
            return Task.CompletedTask;

        // Fire-and-forget on a background Task so host start is not blocked
        // by detection latency. The work is single-shot.
        _ = Task.Run(() => PreloadAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task PreloadAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var orchestrator = scope.ServiceProvider.GetService<BotDetectionOrchestrator>();
            var statsRecorder = scope.ServiceProvider.GetService<Services.IBotDetectionService>();

            if (orchestrator is null)
            {
                _logger.LogDebug("DemoPreload: orchestrator not registered; skipping");
                return;
            }

            foreach (var name in _options.DemoPreloadOnStartup)
            {
                if (ct.IsCancellationRequested) return;

                var ua = ResolveUserAgent(name);
                if (ua is null)
                {
                    _logger.LogWarning(
                        "DemoPreload: '{Name}' not found in TestModeSimulations or built-in defaults; skipping",
                        name);
                    continue;
                }

                try
                {
                    var fake = BuildSyntheticContext(ua, name);
                    var evidence = await orchestrator.DetectAsync(fake, ct);

                    // Tally synthetic preload requests in IBotDetectionService
                    // so /bot-detection/stats reflects the preload work that
                    // populated the dashboard.
                    statsRecorder?.RecordDetection(new Models.BotDetectionResult
                    {
                        IsBot = evidence.BotProbability >= 0.5,
                        ConfidenceScore = evidence.BotProbability,
                        BotType = evidence.PrimaryBotType,
                        BotName = evidence.PrimaryBotName,
                        ProcessingTimeMs = evidence.TotalProcessingTimeMs,
                    });

                    _logger.LogInformation(
                        "DemoPreload: drove '{Name}' through detection (UA='{UA}')", name, ua);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "DemoPreload: detection failed for '{Name}'; continuing", name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DemoPreload: preload aborted");
        }
    }

    private string? ResolveUserAgent(string name)
    {
        if (_options.TestModeSimulations.TryGetValue(name, out var configured))
            return configured;
        if (BuiltInSimulations.TryGetValue(name, out var builtIn))
            return builtIn;
        return null;
    }

    private static HttpContext BuildSyntheticContext(string userAgent, string presetName)
    {
        // DefaultHttpContext gives us a stand-alone context with no Server
        // backing. The orchestrator reads headers + connection metadata; that
        // is enough to drive every detector except the truly client-side ones
        // (which we want to skip for preload anyway).
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString($"stylobot-demo-preload-{presetName}");
        ctx.Request.Path = "/__demo_preload";
        ctx.Request.Headers.UserAgent = userAgent;
        ctx.Request.Headers.Accept = "text/html,*/*";
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.42");
        ctx.Items["__stylobot.demo_preload"] = presetName;
        // Same flag the test-mode middleware uses: preloads must NOT taint
        // pattern/IP reputation or the L1 fingerprint cache. The dashboard's
        // SignatureAggregateCache is populated via a separate path that
        // the ephemeral flag does not gate, so the Top Bots panel still
        // shows the preset fingerprints after restart while the actual
        // localhost visitor stays clean.
        ctx.Items[BotDetectionMiddleware.TestModeEphemeralKey] = true;
        return ctx;
    }
}