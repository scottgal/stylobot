namespace Mostlylucid.BotDetection.Services;

public interface IReactionPackContext
{
    string? GetOverridePolicy(string endpoint, string? currentPolicy);
    IReadOnlyList<(string PackName, int Level, string PolicyName, string Scope)> GetActiveStates();
}
