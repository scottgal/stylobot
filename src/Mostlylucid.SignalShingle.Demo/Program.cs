using Mostlylucid.SignalShingle;
using Mostlylucid.SignalShingle.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalShingleUi(o =>
{
    o.Capacity = 16;
    o.DefaultRefreshInterval = TimeSpan.FromSeconds(2);
    o.MaximumStaleness = TimeSpan.FromSeconds(20);
});
builder.Services.AddHostedService<ClockMaterializer>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapStaticAssets();
app.MapSignalShingleUi();
app.Run();

sealed class ClockMaterializer(
    ISignalShingleCache<string, string> cache,
    ISignalShingleNotifier notifier) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        cache.Pin("clock", TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var candidate in cache.AcquireRefreshCandidates(4))
            {
                var html = $"<strong>Server time: {DateTimeOffset.UtcNow:T}</strong> <small>generation {candidate.CurrentGeneration + 1}</small>";
                if (cache.CompleteRefresh(candidate, html, candidate.CurrentGeneration + 1))
                    await notifier.NotifyAsync(candidate.Key, candidate.CurrentGeneration + 1, stoppingToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
