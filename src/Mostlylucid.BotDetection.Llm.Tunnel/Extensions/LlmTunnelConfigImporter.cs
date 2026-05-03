using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Llm.Tunnel.Extensions;

/// <summary>
/// Imports connection keys from configuration into the node registry on startup.
/// </summary>
public sealed class LlmTunnelConfigImporter(
    ILlmNodeRegistry registry,
    IOptions<LocalLlmTunnelOptions> options,
    ILogger<LlmTunnelConfigImporter> logger)
    : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var keys = new List<string>();

        if (!string.IsNullOrWhiteSpace(opts.ConnectionKey))
            keys.Add(opts.ConnectionKey);

        keys.AddRange(opts.ConnectionKeys.Where(k => !string.IsNullOrWhiteSpace(k)));

        foreach (var key in keys)
        {
            try
            {
                var (descriptor, _) = LlmNodeImporter.ImportKey(key);
                registry.Register(descriptor);
                logger.LogInformation("Imported local LLM tunnel node {NodeId} ({Name}) from configuration.",
                    descriptor.NodeId, descriptor.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to import local LLM tunnel connection key from configuration.");
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
