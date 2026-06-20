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
                    // Stream-count lines instead of loading the whole file into memory
                    // via File.ReadAllLinesAsync. A 533MB jsonl was spiking startup
                    // RSS by half a gigabyte just to produce a line count we then
                    // discarded. StreamReader reads one line at a time with a 4KB
                    // buffer; the line strings are GC'd in the same iteration.
                    var count = 0;
                    await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 4096, useAsync: true);
                    using var reader = new StreamReader(fs);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line)) count++;
                    }
                    var fileName = Path.GetFileName(file);
                    signaturesByDate[fileName] = count;
                    totalSignatures += count;
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