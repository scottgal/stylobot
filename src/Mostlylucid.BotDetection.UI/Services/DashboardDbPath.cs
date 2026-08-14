using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.UI.Services;

internal static class DashboardDbPath
{
    internal static string GetConnectionString(BotDetectionOptions options)
    {
        // GetDirectoryName returns "" (NOT null) for a bare filename like "botdetection.db"
        // (a documented-valid DatabasePath, shipped by the Demo), so the null-coalesce below
        // never fires and Directory.CreateDirectory("") throws at startup. Treat empty as
        // "current base directory", the same as null.
        var dir = Path.GetDirectoryName(
            options.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"));
        var basePath = string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
        Directory.CreateDirectory(basePath);
        return $"Data Source={Path.Combine(basePath, "dashboard.db")};Cache=Shared;Pooling=true";
    }
}
