namespace Mostlylucid.BotDetection.Services;

public interface IStylobotPreActionHook
{
    int Priority { get; }
    ValueTask<string?> GetOverridePolicyAsync(string endpoint, string currentPolicy, CancellationToken ct);
}
