using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     In-process <see cref="ISiteHealthQuery"/>: reads the bounded
///     <see cref="DegradationHistoryAtom"/> ring directly. Used by gateway
///     hosts that own the atom (FOSS standalone dashboard, integration tests).
///     Remote-mode hosts (marketing site, control plane) register
///     <see cref="RemoteSiteHealthQuery"/> instead.
/// </summary>
internal sealed class LocalSiteHealthQuery : ISiteHealthQuery
{
    private readonly DegradationHistoryAtom _history;

    public LocalSiteHealthQuery(DegradationHistoryAtom history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
    }

    public Task<IReadOnlyList<DegradationSnapshot>> GetHistoryAsync(
        string window, CancellationToken ct = default)
    {
        var span = ParseWindow(window ?? "1h");
        var data = _history.GetWindow(DateTime.UtcNow, span);
        return Task.FromResult(data);
    }

    /// <summary>
    ///     Same window mapping as the REST endpoint so local + remote callers
    ///     interpret the dashboard's window token identically. Duplicated
    ///     because <c>Mostlylucid.BotDetection.UI</c> does not (and should not)
    ///     depend on <c>Mostlylucid.BotDetection.Api</c>.
    /// </summary>
    private static TimeSpan ParseWindow(string window) => window switch
    {
        "15m"        => TimeSpan.FromMinutes(15),
        "1h" or "60m" => TimeSpan.FromHours(1),
        "24h" or "1d" => TimeSpan.FromHours(24),
        "7d"         => TimeSpan.FromDays(7),
        _            => TimeSpan.FromHours(1),
    };
}
