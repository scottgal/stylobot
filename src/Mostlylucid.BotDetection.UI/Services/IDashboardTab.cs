namespace Mostlylucid.BotDetection.UI.Services;

public interface IDashboardTab
{
    string TabId { get; }
    string DisplayName { get; }
    string PartialViewPath { get; }
    int Order { get; }
    bool RequiresWrite { get; }
}
