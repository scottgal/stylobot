using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Incoming.Configuration;

namespace Mostlylucid.StyloSpam.Incoming.Connectors;

public sealed class SmtpIncomingConnector : IIncomingConnector
{
    private readonly StyloSpamIncomingOptions _options;

    public SmtpIncomingConnector(IOptions<StyloSpamIncomingOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "Incoming SMTP Proxy";
    public string Protocol => "SMTP";
    public string Mode => "Inbound Relay";
    public bool Enabled => _options.Smtp.Enabled;

    public ValueTask<IncomingConnectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var notes = Enabled
            ? $"Listening on {_options.Smtp.ListenHost}:{_options.Smtp.ListenPort}, upstream {_options.Smtp.UpstreamHost ?? "(not configured)"}:{_options.Smtp.UpstreamPort}"
            : "Disabled by configuration";

        return ValueTask.FromResult(new IncomingConnectorStatus(Name, Protocol, Enabled, Mode, notes));
    }
}
