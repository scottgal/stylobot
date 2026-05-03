namespace Mostlylucid.BotDetection.Api;

public class StyloBotApiOptions
{
    public bool EnableManagementEndpoints { get; set; }
    public bool EnableOpenApi { get; set; } = true;
    public int MaxBatchSize { get; set; } = 100;
    public bool InjectResponseHeaders { get; set; }
    public bool ForwardRequestHeaders { get; set; }
}
