using System.Buffers;
using MimeKit;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;
using Mostlylucid.StyloSpam.Incoming.Configuration;
using Mostlylucid.StyloSpam.Incoming.Services;
using SmtpServer;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace Mostlylucid.StyloSpam.Incoming.Smtp;

public sealed class StyloSpamSmtpMessageStore : MessageStore
{
    private readonly EmailScoringEngine _scoringEngine;
    private readonly IncomingSmtpRelayService _relayService;
    private readonly StyloSpamIncomingOptions _options;
    private readonly ILogger<StyloSpamSmtpMessageStore> _logger;

    public StyloSpamSmtpMessageStore(
        EmailScoringEngine scoringEngine,
        IncomingSmtpRelayService relayService,
        Microsoft.Extensions.Options.IOptions<StyloSpamIncomingOptions> options,
        ILogger<StyloSpamSmtpMessageStore> logger)
    {
        _scoringEngine = scoringEngine;
        _relayService = relayService;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context,
        IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = buffer.ToArray();
            using var stream = new MemoryStream(bytes);
            var message = MimeMessage.Load(stream);

            var metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["smtp.session.id"] = context.SessionId,
                ["smtp.remote.endpoint"] = context.EndpointDefinition?.Endpoint?.ToString() ?? "unknown"
            };

            var envelope = EmailEnvelope.FromMimeMessage(message, EmailFlowMode.Incoming, metadata: metadata);
            var result = await _scoringEngine.EvaluateAsync(envelope, cancellationToken);

            message.Headers.Replace("X-StyloSpam-Score", result.SpamScore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            message.Headers.Replace("X-StyloSpam-Verdict", result.Verdict.ToString());

            if (result.Verdict is SpamVerdict.Block or SpamVerdict.Quarantine && _options.Smtp.QuarantineAsReject)
            {
                _logger.LogWarning(
                    "SMTP message rejected by StyloSpam. Verdict={Verdict}, Score={Score:F3}, From={From}, ToCount={ToCount}",
                    result.Verdict,
                    result.SpamScore,
                    envelope.From,
                    envelope.TotalRecipientCount);

                return SmtpResponse.TransactionFailed;
            }

            var relayed = await _relayService.RelayAsync(message, cancellationToken);
            if (!relayed)
            {
                _logger.LogWarning("SMTP message accepted by StyloSpam but not relayed (relay disabled or not configured).");
            }

            return SmtpResponse.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP proxy failed while processing message.");
            return SmtpResponse.TransactionFailed;
        }
    }
}
