using Mostlylucid.StyloSpam.Incoming.Connectors;

namespace Mostlylucid.StyloSpam.Incoming.Services;

public sealed class IncomingConnectorHostedService : BackgroundService
{
    private readonly ILogger<IncomingConnectorHostedService> _logger;
    private readonly IReadOnlyList<IIncomingConnector> _connectors;

    public IncomingConnectorHostedService(
        ILogger<IncomingConnectorHostedService> logger,
        IEnumerable<IIncomingConnector> connectors)
    {
        _logger = logger;
        _connectors = connectors.ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var statuses = new List<IncomingConnectorStatus>();
        foreach (var connector in _connectors)
        {
            statuses.Add(await connector.GetStatusAsync(stoppingToken));
        }

        foreach (var status in statuses)
        {
            _logger.LogInformation("StyloSpam Incoming connector {Name} [{Protocol}] enabled={Enabled} mode={Mode} notes={Notes}",
                status.Name, status.Protocol, status.Enabled, status.Mode, status.Notes);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
