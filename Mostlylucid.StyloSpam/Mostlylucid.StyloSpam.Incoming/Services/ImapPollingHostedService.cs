using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;
using Mostlylucid.StyloSpam.Incoming.Configuration;

namespace Mostlylucid.StyloSpam.Incoming.Services;

public sealed class ImapPollingHostedService : BackgroundService
{
    private readonly StyloSpamIncomingOptions _options;
    private readonly EmailScoringEngine _engine;
    private readonly ILogger<ImapPollingHostedService> _logger;

    public ImapPollingHostedService(
        IOptions<StyloSpamIncomingOptions> options,
        EmailScoringEngine engine,
        ILogger<ImapPollingHostedService> logger)
    {
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
                _logger.LogWarning(ex, "IMAP polling cycle failed.");
            }

            var interval = Math.Max(15, _options.Imap.PollIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var cfg = _options.Imap;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Host) || string.IsNullOrWhiteSpace(cfg.Username) || string.IsNullOrWhiteSpace(cfg.Password))
        {
            return;
        }

        using var client = new ImapClient();
        await client.ConnectAsync(cfg.Host, cfg.Port, cfg.UseSsl, cancellationToken);
        await client.AuthenticateAsync(cfg.Username, cfg.Password, cancellationToken);

        var folder = await client.GetFolderAsync(cfg.Folder, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        var uids = await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken);
        if (uids.Count == 0)
        {
            await client.DisconnectAsync(true, cancellationToken);
            return;
        }

        var maxToProcess = Math.Max(1, cfg.MaxMessagesPerPoll);
        var selected = uids.Take(maxToProcess).ToList();

        foreach (var uid in selected)
        {
            var message = await folder.GetMessageAsync(uid, cancellationToken);
            var envelope = EmailEnvelope.FromMimeMessage(
                message,
                EmailFlowMode.Incoming,
                metadata: new Dictionary<string, object>
                {
                    ["source"] = "imap",
                    ["imap.uid"] = uid.Id
                });

            var score = await _engine.EvaluateAsync(envelope, cancellationToken);
            _logger.LogInformation(
                "IMAP message scored. Subject={Subject}, Verdict={Verdict}, Score={Score:F3}",
                envelope.Subject,
                score.Verdict,
                score.SpamScore);

            if (score.Verdict is SpamVerdict.Warn or SpamVerdict.Quarantine or SpamVerdict.Block)
            {
                await folder.AddFlagsAsync(uid, MessageFlags.Flagged, true, cancellationToken);
            }

            await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
        }

        await client.DisconnectAsync(true, cancellationToken);
    }
}
