using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Pins the <c>AddBotDetectionInMemory()</c> ephemeral-mode contract: the full
///     detection pipeline wires up with the SQLite-backed stores swapped for their
///     Null / InMemory variants so no .db file is created (CI, integration tests, the
///     <c>stylobot -e / --economy</c> gateway flag). The Step-7 contributor delete
///     collapsed this to a bare alias for AddBotDetection; this test guards the
///     restored behaviour.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EphemeralModeTests
{
    [Fact]
    public void AddBotDetectionInMemory_SwapsSqliteStoresForNullVariants()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddRouting();
        services.AddBotDetectionInMemory();

        using var provider = services.BuildServiceProvider();

        // The three own-path SQLite stores are swapped for Null / InMemory.
        Assert.IsType<NullWeightStore>(provider.GetRequiredService<IWeightStore>());
        Assert.IsType<NullDetectionArchive>(provider.GetRequiredService<IDetectionArchive>());
        Assert.IsType<InMemoryBotListDatabase>(provider.GetRequiredService<IBotListDatabase>());

        // Identity layer parked (its stores open fingerprints.db directly) and the
        // shared Func<DbConnection> factory routed to file::memory: via empty DatabasePath.
        var options = provider.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
        Assert.False(options.Identity.Enabled);
        Assert.True(string.IsNullOrEmpty(options.DatabasePath));
    }

    [Fact]
    public async Task AddBotDetectionInMemory_ServesRequests_WithoutCreatingDbFiles()
    {
        // AppContext.BaseDirectory is the test bin dir; snapshot the known StyloBot
        // db filenames before boot so a concurrent test's unrelated .db can't false-fail.
        string[] knownDbNames =
        [
            "botdetection.db", "sessions.db", "fingerprints.db", "weights.db",
            "clusters.db", "dashboard.db"
        ];
        var dir = AppContext.BaseDirectory;
        var before = knownDbNames.Where(n => File.Exists(Path.Combine(dir, n))).ToHashSet();

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddBotDetectionInMemory();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseBotDetection();
                }))
            .StartAsync();

        var client = host.GetTestClient();

        // A human request and a bot request exercise the fast path + persistence hooks.
        var human = new HttpRequestMessage(HttpMethod.Get, "/");
        human.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        await client.SendAsync(human);

        var bot = new HttpRequestMessage(HttpMethod.Get, "/");
        bot.Headers.UserAgent.ParseAdd("curl/8.4.0");
        await client.SendAsync(bot);

        // Any known StyloBot db that appeared after boot (and wasn't there before) is a leak.
        var leaked = knownDbNames
            .Where(n => File.Exists(Path.Combine(dir, n)))
            .Where(n => !before.Contains(n))
            .ToList();

        Assert.True(leaked.Count == 0,
            $"Ephemeral mode created SQLite files: {string.Join(", ", leaked)}");
    }
}
