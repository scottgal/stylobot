using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Incoming.Configuration;

namespace Mostlylucid.StyloSpam.Incoming.Connectors;

public sealed class ImapIncomingConnector : IIncomingConnector
{
    private readonly StyloSpamIncomingOptions _options;

    public ImapIncomingConnector(IOptions<StyloSpamIncomingOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "IMAP Ingestion Connector";
    public string Protocol => "IMAP";
    public string Mode => "Mailbox Polling";
    public bool Enabled => _options.Imap.Enabled;

    public ValueTask<IncomingConnectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var host = string.IsNullOrWhiteSpace(_options.Imap.Host) ? "(not configured)" : _options.Imap.Host;
        var notes = Enabled
            ? $"Host {host}:{_options.Imap.Port}, SSL={_options.Imap.UseSsl}"
            : "Disabled by configuration";

        return ValueTask.FromResult(new IncomingConnectorStatus(Name, Protocol, Enabled, Mode, notes));
    }
}
