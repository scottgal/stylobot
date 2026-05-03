using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;
using Mostlylucid.StyloSpam.Incoming.Configuration;

namespace Mostlylucid.StyloSpam.Incoming.Services;

public sealed class GmailPollingHostedService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly StyloSpamIncomingOptions _options;
    private readonly EmailScoringEngine _engine;
    private readonly ILogger<GmailPollingHostedService> _logger;

    public GmailPollingHostedService(
        IHttpClientFactory httpClientFactory,
        IOptions<StyloSpamIncomingOptions> options,
        EmailScoringEngine engine,
        ILogger<GmailPollingHostedService> logger)
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
                _logger.LogWarning(ex, "Gmail polling cycle failed.");
            }

            var interval = Math.Max(30, _options.Gmail.PollIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var cfg = _options.Gmail;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.UserEmail) || string.IsNullOrWhiteSpace(cfg.AccessToken))
        {
            return;
        }

        var client = _httpClientFactory.CreateClient("gmail");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.AccessToken);

        var listUrl = $"https://gmail.googleapis.com/gmail/v1/users/{Uri.EscapeDataString(cfg.UserEmail)}/messages?maxResults={Math.Max(1, cfg.MaxMessagesPerPoll)}&q=is:unread";
        using var listResponse = await client.GetAsync(listUrl, cancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Gmail list query failed with status {StatusCode}", (int)listResponse.StatusCode);
            return;
        }

        var payload = await listResponse.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("messages", out var messagesElement) || messagesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var messageNode in messagesElement.EnumerateArray())
        {
            if (!messageNode.TryGetProperty("id", out var idNode))
            {
                continue;
            }

            var id = idNode.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var messageUrl = $"https://gmail.googleapis.com/gmail/v1/users/{Uri.EscapeDataString(cfg.UserEmail)}/messages/{Uri.EscapeDataString(id)}?format=raw";
            using var messageResponse = await client.GetAsync(messageUrl, cancellationToken);
            if (!messageResponse.IsSuccessStatusCode)
            {
                continue;
            }

            var messageJson = await messageResponse.Content.ReadAsStringAsync(cancellationToken);
            using var messageDoc = JsonDocument.Parse(messageJson);
            if (!messageDoc.RootElement.TryGetProperty("raw", out var rawNode))
            {
                continue;
            }

            var raw = rawNode.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var mime = DecodeBase64Url(raw);
            var envelope = EmailEnvelopeFactory.FromRawMime(
                mime,
                EmailFlowMode.Incoming,
                metadata: new Dictionary<string, object>
                {
                    ["source"] = "gmail",
                    ["gmail.message_id"] = id
                });

            var score = await _engine.EvaluateAsync(envelope, cancellationToken);
            _logger.LogInformation("Gmail message scored. Subject={Subject}, Verdict={Verdict}, Score={Score:F3}",
                envelope.Subject, score.Verdict, score.SpamScore);
        }
    }

    private static string DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
        }

        var bytes = Convert.FromBase64String(normalized);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
