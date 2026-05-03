using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Mostlylucid.StyloSpam.Incoming.Configuration;

namespace Mostlylucid.StyloSpam.Incoming.Services;

public sealed class IncomingSmtpRelayService
{
    private readonly StyloSpamIncomingOptions _options;
    private readonly ILogger<IncomingSmtpRelayService> _logger;

    public IncomingSmtpRelayService(
        IOptions<StyloSpamIncomingOptions> options,
        ILogger<IncomingSmtpRelayService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> RelayAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        var smtp = _options.Smtp;
        if (!smtp.Enabled || !smtp.RelayEnabled || string.IsNullOrWhiteSpace(smtp.UpstreamHost))
        {
            _logger.LogWarning("Incoming relay skipped: SMTP relay disabled or upstream host not configured.");
            return false;
        }

        using var client = new SmtpClient();

        var secureSocketOption = smtp.UseStartTls
            ? SecureSocketOptions.StartTlsWhenAvailable
            : SecureSocketOptions.Auto;

        await client.ConnectAsync(smtp.UpstreamHost, smtp.UpstreamPort, secureSocketOption, cancellationToken);

        if (!string.IsNullOrWhiteSpace(smtp.UpstreamUsername) && !string.IsNullOrWhiteSpace(smtp.UpstreamPassword))
        {
            await client.AuthenticateAsync(smtp.UpstreamUsername, smtp.UpstreamPassword, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        return true;
    }
}
