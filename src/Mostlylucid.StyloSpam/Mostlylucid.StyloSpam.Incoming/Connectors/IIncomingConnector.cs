namespace Mostlylucid.StyloSpam.Incoming.Connectors;

public sealed record IncomingConnectorStatus(
    string Name,
    string Protocol,
    bool Enabled,
    string Mode,
    string Notes);

public interface IIncomingConnector
{
    string Name { get; }
    string Protocol { get; }
    string Mode { get; }
    bool Enabled { get; }

    ValueTask<IncomingConnectorStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
