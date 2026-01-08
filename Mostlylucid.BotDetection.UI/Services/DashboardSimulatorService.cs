using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Simulator service for generating fake bot detection events.
///     Useful for testing the dashboard without real traffic.
///     TODO: Integrate with LLMApi (https://github.com/scottgal/LLMApi) for more realistic mocking.
/// </summary>
public class DashboardSimulatorService : BackgroundService
{
    private readonly string[] _actions = { "Allow", "Block", "Throttle", "Challenge" };

    private readonly string[] _botNames =
        { "Googlebot", "Bingbot", "Malicious Scraper", "DDoS Bot", "SEO Crawler", "Unknown" };

    private readonly string[] _botTypes = { "Crawler", "Scraper", "Scanner", "BadBot", "GoodBot" };
    private readonly IDashboardEventStore _eventStore;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly ILogger<DashboardSimulatorService> _logger;
    private readonly string[] _methods = { "GET", "POST", "PUT", "DELETE" };
    private readonly StyloBotDashboardOptions _options;

    private readonly string[] _paths =
        { "/api/products", "/api/users", "/", "/search", "/login", "/checkout", "/api/orders" };

    private readonly Random _random = new();
    private readonly string[] _riskBands = { "VeryLow", "Low", "Medium", "High", "VeryHigh" };

    public DashboardSimulatorService(
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        StyloBotDashboardOptions options,
        ILogger<DashboardSimulatorService> logger)
    {
        _hubContext = hubContext;
        _eventStore = eventStore;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Dashboard simulator started (rate: {Rate} events/sec)",
            _options.SimulatorEventsPerSecond);

        var interval = TimeSpan.FromSeconds(1.0 / _options.SimulatorEventsPerSecond);

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                // Generate detection event
                var detection = GenerateDetectionEvent();
                await _eventStore.AddDetectionAsync(detection);
                await _hubContext.Clients.All.BroadcastDetection(detection);

                // 20% chance to generate signature event
                if (_random.NextDouble() < 0.2)
                {
                    var signature = GenerateSignatureEvent();
                    await _eventStore.AddSignatureAsync(signature);
                    await _hubContext.Clients.All.BroadcastSignature(signature);
                }

                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in simulator");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }

        _logger.LogInformation("Dashboard simulator stopped");
    }

    private DashboardDetectionEvent GenerateDetectionEvent()
    {
        var isBot = _random.NextDouble() < 0.3; // 30% bot traffic
        var botProbability = isBot
            ? 0.7 + _random.NextDouble() * 0.3 // 0.7-1.0
            : _random.NextDouble() * 0.3; // 0-0.3

        var confidence = 0.6 + _random.NextDouble() * 0.4; // 0.6-1.0
        var riskBand = DetermineRiskBand(botProbability);

        var detection = new DashboardDetectionEvent
        {
            RequestId = Guid.NewGuid().ToString("N")[..16],
            Timestamp = DateTime.UtcNow,
            IsBot = isBot,
            BotProbability = botProbability,
            Confidence = confidence,
            RiskBand = riskBand,
            BotType = isBot ? _botTypes[_random.Next(_botTypes.Length)] : null,
            BotName = isBot ? _botNames[_random.Next(_botNames.Length)] : null,
            Action = DetermineAction(riskBand),
            PolicyName = "Default",
            Method = _methods[_random.Next(_methods.Length)],
            Path = _paths[_random.Next(_paths.Length)],
            StatusCode = DetermineStatusCode(riskBand),
            ProcessingTimeMs = 1 + _random.NextDouble() * 50,
            IpAddress = GenerateIpAddress(),
            UserAgent = GenerateUserAgent(isBot),
            TopReasons = GenerateReasons(isBot),
            PrimarySignature = GenerateSignatureHash()
        };

        return detection;
    }

    private DashboardSignatureEvent GenerateSignatureEvent()
    {
        var isKnownBot = _random.NextDouble() < 0.4;
        var hitCount = _random.Next(1, 100);
        var riskBand = _riskBands[_random.Next(_riskBands.Length)];

        return new DashboardSignatureEvent
        {
            SignatureId = Guid.NewGuid().ToString("N")[..16],
            Timestamp = DateTime.UtcNow,
            PrimarySignature = GenerateSignatureHash(),
            IpSignature = GenerateSignatureHash(),
            UaSignature = GenerateSignatureHash(),
            ClientSideSignature = _random.NextDouble() < 0.5 ? GenerateSignatureHash() : null,
            FactorCount = _random.Next(2, 6),
            RiskBand = riskBand,
            HitCount = hitCount,
            IsKnownBot = isKnownBot,
            BotName = isKnownBot ? _botNames[_random.Next(_botNames.Length)] : null
        };
    }

    private string DetermineRiskBand(double probability)
    {
        return probability switch
        {
            >= 0.9 => "VeryHigh",
            >= 0.7 => "High",
            >= 0.5 => "Medium",
            >= 0.3 => "Low",
            _ => "VeryLow"
        };
    }

    private string DetermineAction(string riskBand)
    {
        return riskBand switch
        {
            "VeryHigh" => _random.NextDouble() < 0.8 ? "Block" : "Challenge",
            "High" => _random.NextDouble() < 0.5 ? "Throttle" : "Challenge",
            "Medium" => _random.NextDouble() < 0.7 ? "Allow" : "Throttle",
            _ => "Allow"
        };
    }

    private int DetermineStatusCode(string riskBand)
    {
        return riskBand switch
        {
            "VeryHigh" => _random.NextDouble() < 0.8 ? 403 : 429,
            "High" => _random.NextDouble() < 0.5 ? 429 : 200,
            _ => 200
        };
    }

    private string GenerateIpAddress()
    {
        return $"{_random.Next(1, 256)}.{_random.Next(1, 256)}.{_random.Next(1, 256)}.{_random.Next(1, 256)}";
    }

    private string GenerateUserAgent(bool isBot)
    {
        if (isBot)
        {
            var botUAs = new[]
            {
                "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
                "Mozilla/5.0 (compatible; Bingbot/2.0; +http://www.bing.com/bingbot.htm)",
                "python-requests/2.28.0",
                "curl/7.68.0",
                "Scrapy/2.6.0"
            };
            return botUAs[_random.Next(botUAs.Length)];
        }

        var browsers = new[]
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0"
        };
        return browsers[_random.Next(browsers.Length)];
    }

    private List<string> GenerateReasons(bool isBot)
    {
        if (isBot)
        {
            var reasons = new[]
            {
                "Known bot user agent pattern",
                "Suspicious request rate",
                "Missing browser fingerprint",
                "Automated tool signature",
                "Blocked IP range"
            };
            return reasons.OrderBy(_ => _random.Next()).Take(_random.Next(1, 4)).ToList();
        }

        return new List<string> { "Normal browser behavior" };
    }

    private string GenerateSignatureHash()
    {
        var bytes = new byte[16];
        _random.NextBytes(bytes);
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}