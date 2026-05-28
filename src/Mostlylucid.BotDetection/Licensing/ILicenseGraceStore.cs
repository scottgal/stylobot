namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Persistence contract for the license-grace start timestamp. When a
///     license validation fails the gateway enters a configurable grace
///     window; this store records when that window began so the timer
///     survives a restart. Default FOSS binding is
///     <see cref="SqliteLicenseGraceStore"/>; commercial gateways swap in
///     a Postgres implementation via DI replace.
/// </summary>
public interface ILicenseGraceStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<DateTimeOffset?> GetGraceStartedAtAsync(CancellationToken ct = default);
    Task SetGraceStartedAtAsync(DateTimeOffset value, CancellationToken ct = default);
    Task ClearGraceStartedAtAsync(CancellationToken ct = default);
}
