using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Incoming.Configuration;

namespace Mostlylucid.StyloSpam.Incoming.Connectors;

public sealed class GmailIncomingConnector : IIncomingConnector
{
    private readonly StyloSpamIncomingOptions _options;

    public GmailIncomingConnector(IOptions<StyloSpamIncomingOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "Gmail API Connector";
    public string Protocol => "Gmail API";
    public string Mode => "API Mailbox Ingestion";
    public bool Enabled => _options.Gmail.Enabled;

    public ValueTask<IncomingConnectorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var user = string.IsNullOrWhiteSpace(_options.Gmail.UserEmail) ? "(not configured)" : _options.Gmail.UserEmail;
        var notes = Enabled
            ? $"Configured user {user}, project {_options.Gmail.ProjectId ?? "(not configured)"}"
            : "Disabled by configuration";

        return ValueTask.FromResult(new IncomingConnectorStatus(Name, Protocol, Enabled, Mode, notes));
    }
}
