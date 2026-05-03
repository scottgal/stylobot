using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Incoming.Configuration;
using Mostlylucid.StyloSpam.Incoming.Smtp;
using SmtpServer;
using SmtpServer.ComponentModel;

namespace Mostlylucid.StyloSpam.Incoming.Services;

public sealed class SmtpProxyHostedService : BackgroundService
{
    private readonly StyloSpamIncomingOptions _options;
    private readonly StyloSpamSmtpMessageStore _messageStore;
    private readonly ILogger<SmtpProxyHostedService> _logger;

    public SmtpProxyHostedService(
        IOptions<StyloSpamIncomingOptions> options,
        StyloSpamSmtpMessageStore messageStore,
        ILogger<SmtpProxyHostedService> logger)
    {
        _options = options.Value;
        _messageStore = messageStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Smtp.Enabled)
        {
            _logger.LogInformation("SMTP proxy disabled by configuration.");
            return;
        }

        var builder = new SmtpServerOptionsBuilder()
            .ServerName("StyloSpam-Incoming")
            .Port(_options.Smtp.ListenPort, false);

        var serviceProvider = new SmtpServer.ComponentModel.ServiceProvider();
        serviceProvider.Add(_messageStore);

        var server = new SmtpServer.SmtpServer(builder.Build(), serviceProvider);

        _logger.LogInformation(
            "Starting StyloSpam SMTP proxy on {Host}:{Port}",
            _options.Smtp.ListenHost,
            _options.Smtp.ListenPort);

        await server.StartAsync(stoppingToken);
    }
}
