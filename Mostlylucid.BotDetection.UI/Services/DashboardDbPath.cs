using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.UI.Services;

internal static class DashboardDbPath
{
    internal static string GetConnectionString(BotDetectionOptions options)
    {
        var basePath = Path.GetDirectoryName(
            options.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        return $"Data Source={Path.Combine(basePath, "dashboard.db")};Cache=Shared";
    }
}
