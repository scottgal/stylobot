using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Outgoing.Configuration;

namespace Mostlylucid.StyloSpam.Outgoing.Services;

public sealed class OutgoingRelayService
{
    private readonly StyloSpamOutgoingOptions _options;
    private readonly ILogger<OutgoingRelayService> _logger;

    public OutgoingRelayService(
        IOptions<StyloSpamOutgoingOptions> options,
        ILogger<OutgoingRelayService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> RelayAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        var cfg = _options.Relay;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Host))
        {
            _logger.LogWarning("Outgoing relay skipped: relay disabled or host not configured.");
            return false;
        }

        using var client = new SmtpClient();
        var socket = cfg.UseStartTls ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.Auto;

        await client.ConnectAsync(cfg.Host, cfg.Port, socket, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cfg.Username) && !string.IsNullOrWhiteSpace(cfg.Password))
        {
            await client.AuthenticateAsync(cfg.Username, cfg.Password, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return true;
    }

    public bool CanRelay(SpamVerdict verdict)
    {
        return verdict switch
        {
            SpamVerdict.Allow => true,
            SpamVerdict.Tag => true,
            SpamVerdict.Warn => _options.Relay.RelayOnWarn,
            SpamVerdict.Quarantine => false,
            SpamVerdict.Block => false,
            _ => false
        };
    }
}
