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
    public static Task LoadSignaturesFromJsonL(IServiceProvider services, ILogger logger)
    {
        try
        {
            // Find all signatures-*.jsonl files in the current directory
            var signatureFiles = Directory.GetFiles(".", "signatures-*.jsonl");
            if (signatureFiles.Length == 0)
            {
                logger.Information("No signature files found");
                return Task.CompletedTask;
            }

            // Report the on-disk footprint from file metadata only. The previous
            // implementation stream-read every line of every file at boot just to
            // produce a signature count it logged and discarded; on a long-running
            // gateway that meant reading multi-GB of JSONL before serving the first
            // request (a soak accumulated ~14 GB and stalled startup here).
            // FileInfo.Length reads no file bytes, so boot is O(files) not O(bytes).
            // The footprint itself is bounded by SignatureJsonlRetentionGuardian.
            long totalBytes = 0;
            foreach (var file in signatureFiles)
                try
                {
                    totalBytes += new FileInfo(file).Length;
                }
                catch (Exception fileEx)
                {
                    logger.Warning(fileEx, "Failed to stat signature file {File}", Path.GetFileName(file));
                }

            logger.Information(
                "Found {FileCount} signature file(s), {TotalMB} MB on disk",
                signatureFiles.Length, totalBytes / 1_048_576);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to enumerate signature JSON-L files");
        }

        return Task.CompletedTask;
    }
}