using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;
using Mostlylucid.StyloSpam.Incoming.Configuration;

namespace Mostlylucid.StyloSpam.Incoming.Services;

public sealed class OutlookPollingHostedService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StyloSpamIncomingOptions _options;
    private readonly EmailScoringEngine _engine;
    private readonly ILogger<OutlookPollingHostedService> _logger;

    public OutlookPollingHostedService(
        IHttpClientFactory httpClientFactory,
        IOptions<StyloSpamIncomingOptions> options,
        EmailScoringEngine engine,
        ILogger<OutlookPollingHostedService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outlook polling cycle failed.");
            }

            var interval = Math.Max(30, _options.Outlook.PollIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var cfg = _options.Outlook;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.MailboxUserPrincipalName) || string.IsNullOrWhiteSpace(cfg.AccessToken))
        {
            return;
        }

        var client = _httpClientFactory.CreateClient("outlook");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AccessToken);

        var top = Math.Max(1, cfg.MaxMessagesPerPoll);
        var listUrl = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(cfg.MailboxUserPrincipalName)}/mailFolders/inbox/messages?$top={top}&$select=id,subject";
        using var listResponse = await client.GetAsync(listUrl, cancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Outlook message list query failed with status {StatusCode}", (int)listResponse.StatusCode);
            return;
        }

        var payload = await listResponse.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var message in values.EnumerateArray())
        {
            if (!message.TryGetProperty("id", out var idNode))
            {
                continue;
            }

            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var mimeUrl = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(cfg.MailboxUserPrincipalName)}/messages/{Uri.EscapeDataString(id)}/$value";
            using var mimeResponse = await client.GetAsync(mimeUrl, cancellationToken);
            if (!mimeResponse.IsSuccessStatusCode)
            {
                continue;
            }

            var rawMime = await mimeResponse.Content.ReadAsStringAsync(cancellationToken);
            var envelope = EmailEnvelopeFactory.FromRawMime(
                rawMime,
                EmailFlowMode.Incoming,
                metadata: new Dictionary<string, object>
                {
                    ["source"] = "outlook",
                    ["outlook.message_id"] = id
                });

            var score = await _engine.EvaluateAsync(envelope, cancellationToken);
            _logger.LogInformation("Outlook message scored. Subject={Subject}, Verdict={Verdict}, Score={Score:F3}",
                envelope.Subject, score.Verdict, score.SpamScore);
        }
    }
}
