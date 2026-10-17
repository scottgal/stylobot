using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Storage;
using Mostlylucid.Common.Scheduling;

// A real child process: the caller checks the OS exit status, not a mocked
// StopApplication. No reflection into the watchdog or changes to its timer loop.
if (args.Length != 2 || args[0] is not ("silent" or "storm" or "healthy" or "disabled" or "schema" or "schema-restart"))
    return 2;

var scenario = args[0];
try
{
    using var host = new HostBuilder()
        .ConfigureServices(services =>
        {
            services.AddBotDetection(options => options.DatabasePath = Path.Combine(args[1], "botdetection.db"));
            // Keep production startup registrations for the part under test.
            // Exclude network fetchers and other unrelated background work.
            foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray())
            {
                var type = descriptor.ImplementationType;
                var keep = scenario.StartsWith("schema", StringComparison.Ordinal)
                    ? type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(StoreInitService<>)
                    : type?.Name == "ScheduleCoordinatorWatchdog";
                if (!keep) services.Remove(descriptor);
            }
            services.Replace(ServiceDescriptor.Singleton<IScheduleCoordinator>(new HarnessCoordinator(scenario)));
            services.PostConfigure<ScheduleCoordinatorOptions>(options =>
            {
                options.WatchdogStartupGrace = TimeSpan.Zero;
                options.WatchdogCheckInterval = TimeSpan.FromMilliseconds(25);
                options.WatchdogStalenessMultiplier = scenario switch
                {
                    "silent" => 0.01,
                    "healthy" => 0.05,
                    _ => 1000
                };
                options.WatchdogFaultStormThreshold = 1;
                if (scenario == "disabled")
                {
                    options.EnableTick10s = false;
                    options.EnableTick1m = false;
                    options.EnableTick5m = false;
                    options.EnableTick1h = false;
                    options.WatchdogStalenessMultiplier = 0.01;
                }
            });
        })
        .Build();

    // Bound failures without turning a hung watchdog into a passing scenario.
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await host.StartAsync(deadline.Token);
    if (scenario.StartsWith("schema", StringComparison.Ordinal))
    {
        foreach (var name in new[] { "waveform_history", "pool_collisions", "signature_centroids", "session_centroids", "intent_centroids" })
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(args[1], name + ".db"),
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(deadline.Token)) != 1)
                throw new InvalidOperationException($"Missing schema: {name}");
        }

        var waveform = host.Services.GetRequiredService<WaveformHistoryStore>();
        if (scenario == "schema-restart")
        {
            var history = await waveform.GetAsync("lifecycle", deadline.Token);
            if (history?.Snapshots.Count != 1 || history.Snapshots[0].Path != "/lifecycle")
                throw new InvalidOperationException("Waveform history did not survive process restart");
        }
        else
        {
            host.Services.GetRequiredService<IWaveformHistoryStore>().Record("lifecycle", new RequestSnapshot
            {
                Timestamp = DateTimeOffset.UtcNow, Path = "/lifecycle", Method = "GET",
                StatusCode = 200, UserAgent = "LifecycleHarness", RefererHash = "", ContentClass = ContentClass.Page
            });
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(args[1], "waveform_history.db")};Mode=ReadOnly");
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM waveform_history WHERE signature = 'lifecycle'";
            while (Convert.ToInt64(await command.ExecuteScalarAsync(deadline.Token)) != 1)
                await Task.Delay(25, deadline.Token);
        }
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
    }
    else if (scenario is "healthy" or "disabled")
    {
        var coordinator = (HarnessCoordinator)host.Services.GetRequiredService<IScheduleCoordinator>();
        // Run longer than the 500ms silence threshold, keeping real heartbeats
        // fresh. This detects watchdogs that stop a host despite flowing ticks.
        for (var tick = 0; tick < 30; tick++)
        {
            if (scenario == "healthy") await coordinator.TickAsync();
            await Task.Delay(25, deadline.Token);
        }
        if (host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested)
            throw new InvalidOperationException("Healthy host was stopped by the watchdog");
        host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
    }

    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var registration = lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());
    await stopped.Task.WaitAsync(deadline.Token);
    await host.StopAsync(deadline.Token);
    Console.WriteLine($"Lifecycle harness completed: {scenario}");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 2; // Distinct from the watchdog's intentional failure status.
}

// Task<int> entry points must propagate this; returning 0 erases the failure.
return Environment.ExitCode;

sealed class HarnessCoordinator(string scenario) : IScheduleCoordinator
{
    private int _checks;
    private readonly List<Func<DateTimeOffset, CancellationToken, Task>> _handlers = [];

    public IDisposable Subscribe(TickCadence cadence, string subscriberName, CostHint costHint,
        Func<DateTimeOffset, CancellationToken, Task> handler)
    {
        _handlers.Add(handler);
        return new Subscription();
    }

    public Task TickAsync() => Task.WhenAll(_handlers.Select(handler => handler(DateTimeOffset.UtcNow, CancellationToken.None)));

    public IReadOnlyList<TickSubscriberMetadata> Snapshot() => scenario == "storm"
        ? [new(TickCadence.Tick10s, "FaultingSubscriber", CostHint.Low, null, null, 0, Interlocked.Increment(ref _checks))]
        : [];

    private sealed class Subscription : IDisposable
    {
        public void Dispose() { }
    }
}
