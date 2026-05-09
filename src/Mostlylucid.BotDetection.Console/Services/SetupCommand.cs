using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Console.Services;

public static class SetupCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var checkOnly = args.Contains("--check-only", StringComparer.OrdinalIgnoreCase);
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);

        // Batteries.Init() is already called at the top of Program.cs before subcommand dispatch.

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddEnvironmentVariables("STYLOBOT_")
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddHttpClient();
        services.AddMemoryCache();
        services.AddOptions<BotDetectionOptions>().BindConfiguration("BotDetection");
        services.Configure<BotDetectionOptions>(config.GetSection("BotDetection"));
        services.PostConfigure<BotDetectionOptions>(opts =>
        {
            opts.DatabasePath ??= Path.Combine(
                BotDetectionOptions.ResolveDataDirectory(), "botdetection.db");
        });

        services.AddBotDetectionSetupServices();

        await using var sp = services.BuildServiceProvider();
        var setup = sp.GetRequiredService<SetupService>();

        System.Console.WriteLine();
        System.Console.WriteLine("  stylobot setup -- checking resources");
        System.Console.WriteLine();

        var statuses = await setup.CheckAllAsync();

        foreach (var status in statuses)
        {
            var icon = status.Presence switch
            {
                ResourcePresence.Fresh   => "[ok]   ",
                ResourcePresence.Stale   => "[stale]",
                ResourcePresence.Missing => "[miss] ",
                _                        => "       "
            };
            System.Console.WriteLine($"  {icon}  {status.Name}");
            if (status.Detail != null)
                System.Console.WriteLine($"           {status.Detail}");
        }

        System.Console.WriteLine();

        if (checkOnly)
            return 0;

        var needsDownload = statuses.Where(s => s.Presence != ResourcePresence.Fresh || force).ToList();
        if (needsDownload.Count == 0)
        {
            System.Console.WriteLine("  All resources are up to date. Nothing to download.");
            System.Console.WriteLine("  Run with --force to re-download anyway.");
            return 0;
        }

        System.Console.WriteLine($"  Downloading {needsDownload.Count} resource(s)...");
        System.Console.WriteLine();

        var progress = new Progress<string>(msg => System.Console.WriteLine($"  {msg}"));

        try
        {
            await setup.DownloadMissingAsync(progress, force);
            System.Console.WriteLine();
            System.Console.WriteLine("  Setup complete. Run 'stylobot' to start.");
            return 0;
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"  Setup failed: {ex.Message}");
            return 1;
        }
    }
}
