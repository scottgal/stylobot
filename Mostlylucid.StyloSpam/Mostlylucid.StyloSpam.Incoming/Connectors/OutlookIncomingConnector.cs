using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Incoming.Configuration;

namespace Mostlylucid.StyloSpam.Incoming.Connectors;

public sealed class OutlookIncomingConnector : IIncomingConnector
{
    private readonly StyloSpamIncomingOptions _options;

    public OutlookIncomingConnector(IOptions<StyloSpamIncomingOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "Outlook Graph Connector";
    public string Protocol => "Microsoft Graph";
    public string Mode => "API Mailbox Ingestion";
    public bool Enabled => _options.Outlook.Enabled;

    public ValueTask<IncomingConnectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var mailbox = string.IsNullOrWhiteSpace(_options.Outlook.MailboxUserPrincipalName)
            ? "(not configured)"
            : _options.Outlook.MailboxUserPrincipalName;
        var notes = Enabled
            ? $"Mailbox {mailbox}, tenant {_options.Outlook.TenantId ?? "(not configured)"}"
            : "Disabled by configuration";

        return ValueTask.FromResult(new IncomingConnectorStatus(Name, Protocol, Enabled, Mode, notes));
    }
}
