using ILogger = Serilog.ILogger;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Service to load bot signatures from JSON-L files on startup
/// </summary>
public static class SignatureLoaderService
{
    /// <summary>
    ///     Load signatures from JSON-L files on startup
    /// </summary>
    public static async Task LoadSignaturesFromJsonL(IServiceProvider services, ILogger logger)
    {
        try
        {
            // Find all signatures-*.jsonl files in the current directory
            var signatureFiles = Directory.GetFiles(".", "signatures-*.jsonl");
            if (signatureFiles.Length == 0)
            {
                logger.Information("No signature files found");
                return;
            }

            var totalSignatures = 0;
            var signaturesByDate = new Dictionary<string, int>();

            foreach (var file in signatureFiles)
                try
                {
                    var lines = await File.ReadAllLinesAsync(file);
                    var fileName = Path.GetFileName(file);
                    signaturesByDate[fileName] = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Count();
                    totalSignatures += signaturesByDate[fileName];
                }
                catch (Exception fileEx)
                {
                    logger.Warning(fileEx, "Failed to read signature file {File}", Path.GetFileName(file));
                }

            logger.Information("Found {TotalSignatures} signatures across {FileCount} file(s)",
                totalSignatures, signatureFiles.Length);

            foreach (var kvp in signaturesByDate.OrderBy(x => x.Key))
                logger.Debug("  {File}: {Count} signatures", kvp.Key, kvp.Value);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to load signatures from JSON-L files");
        }
    }
}