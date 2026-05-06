namespace Mostlylucid.BotDetection.Services;

public interface IReactionPackContext
{
    string? GetOverridePolicy(string endpoint, string? currentPolicy);
}
