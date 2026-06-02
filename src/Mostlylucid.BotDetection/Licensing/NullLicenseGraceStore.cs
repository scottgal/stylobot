namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Ephemeral-mode no-op: grace state never persists, so a restart always
///     re-enters the grace window cleanly. Acceptable for FOSS where licensing
///     is informational only.
/// </summary>
public sealed class NullLicenseGraceStore : ILicenseGraceStore
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<DateTimeOffset?> GetGraceStartedAtAsync(CancellationToken ct = default)
        => Task.FromResult<DateTimeOffset?>(null);

    public Task SetGraceStartedAtAsync(DateTimeOffset value, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearGraceStartedAtAsync(CancellationToken ct = default) => Task.CompletedTask;
}
