using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Runs every registered <see cref="ISetupResource"/> (bot list, ONNX, GeoIP CSV,
///     future packs) through Check → Download with each one surfaced as its own row
///     in the startup banner. Missing or stale resources are downloaded; fresh ones
///     are marked Skipped with an explanation. Each resource's <c>IProgress&lt;string&gt;</c>
///     callbacks update the row's detail line in real time so multi-MB pulls show
///     "Downloading 12MB/27MB (44%)" rather than freezing.
/// </summary>
public static class StartupSetupBridge
{
    public static async Task RunAsync(
        IServiceProvider services,
        StartupStatusBoard board,
        CancellationToken ct = default)
    {
        var resources = services.GetServices<ISetupResource>().ToList();
        if (resources.Count == 0) return;

        foreach (var resource in resources)
        {
            var taskId = "setup-" + SanitizeId(resource.Name);
            board.Start(taskId, resource.Name, "checking");

            ResourceStatus status;
            try
            {
                status = await resource.CheckAsync(ct);
            }
            catch (Exception ex)
            {
                board.Fail(taskId, "check failed: " + ex.Message);
                continue;
            }

            if (status.Presence == ResourcePresence.Fresh)
            {
                board.Skip(taskId, resource.Name, status.Detail ?? "already fresh");
                continue;
            }

            board.Start(taskId, resource.Name,
                status.Presence == ResourcePresence.Missing ? "downloading (first run)" : "refreshing (stale)");

            var progress = new Progress<string>(msg =>
                board.Start(taskId, resource.Name, msg));

            try
            {
                await resource.DownloadAsync(progress, ct);
                board.Done(taskId, "ready");
            }
            catch (OperationCanceledException)
            {
                board.Fail(taskId, "cancelled");
                throw;
            }
            catch (Exception ex)
            {
                // Non-fatal: detection runs with whatever data is already on disk.
                board.Fail(taskId, ex.Message);
            }
        }
    }

    private static string SanitizeId(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars);
    }
}
