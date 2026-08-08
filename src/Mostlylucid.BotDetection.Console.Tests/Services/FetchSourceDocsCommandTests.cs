using Mostlylucid.BotDetection.Console.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Console.Tests.Services;

/// <summary>
///     <see cref="FetchSourceDocsCommand"/> generates docs/fetch-sources.md straight from
///     IFetchSourceRegistry - these tests guard the CLI wiring (flag parsing, exit codes,
///     the file actually landing) so a refactor can't silently break `stylobot
///     --output-fetch-sources-doc` without a red test, the same way ConfigOutputCommand's
///     equivalent flag is guarded by usage rather than a dedicated unit test today.
/// </summary>
public sealed class FetchSourceDocsCommandTests
{
    [Fact]
    public void TryGetOutputPath_returns_the_path_when_the_flag_is_present()
    {
        var path = FetchSourceDocsCommand.TryGetOutputPath(["stylobot", "--output-fetch-sources-doc", "/tmp/x.md"]);
        Assert.Equal("/tmp/x.md", path);
    }

    [Fact]
    public void TryGetOutputPath_returns_null_when_the_flag_is_absent()
    {
        var path = FetchSourceDocsCommand.TryGetOutputPath(["stylobot", "8080", "http://localhost:9000"]);
        Assert.Null(path);
    }

    [Fact]
    public void TryGetOutputPath_returns_null_when_the_flag_is_the_last_arg_with_no_value()
    {
        var path = FetchSourceDocsCommand.TryGetOutputPath(["stylobot", "--output-fetch-sources-doc"]);
        Assert.Null(path);
    }

    [Fact]
    public void WriteDoc_writes_a_generated_markdown_file_covering_the_known_buckets()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"fetch-sources-doc-{Guid.NewGuid():N}.md");
        var dbPath = Path.Combine(Path.GetTempPath(), $"fetch-sources-doc-db-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("BotDetection__DatabasePath", dbPath);
        try
        {
            var exitCode = FetchSourceDocsCommand.WriteDoc(outputPath, []);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(outputPath));

            var content = File.ReadAllText(outputPath);
            Assert.Contains("# External Fetch Sources", content);
            Assert.Contains("Do not hand-edit", content);
            // Both list_updates buckets must be documented - the exact "expose the 2 real
            // buckets, not false per-source precision" surface this doc exists to describe.
            Assert.Contains("`BotPatternsGroup`", content);
            Assert.Contains("`DatacenterIpsGroup`", content);
            Assert.Contains("Covers**: IsBot, Matomo, CrawlerUserAgents", content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BotDetection__DatabasePath", null);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void WriteDoc_fails_loudly_rather_than_silently_when_DatabasePath_is_unconfigured()
    {
        // Same real options-validation the running gateway hits - this command must not
        // swallow it into a half-written or empty doc.
        var outputPath = Path.Combine(Path.GetTempPath(), $"fetch-sources-doc-{Guid.NewGuid():N}.md");
        Environment.SetEnvironmentVariable("BotDetection__DatabasePath", null);
        try
        {
            var exitCode = FetchSourceDocsCommand.WriteDoc(outputPath, []);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
