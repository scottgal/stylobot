namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Sqlite-free no-op fingerprint approval store. Approvals are an
///     operator-driven manual workflow; commercial gateways register this
///     so no Sqlite file is opened until the Postgres-backed approval
///     store lands. Reads return null/empty; writes drop. Token issuance
///     returns an empty string so callers handle "approval surface is
///     unavailable" without crashing.
/// </summary>
public sealed class NullFingerprintApprovalStore : IFingerprintApprovalStore
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<ApprovalRecord> UpsertAsync(ApprovalRecord record, CancellationToken ct = default)
        => Task.FromResult(record);

    public Task<ApprovalRecord?> GetAsync(string signature, CancellationToken ct = default)
        => Task.FromResult<ApprovalRecord?>(null);

    public Task<IReadOnlyList<ApprovalRecord>> ListRecentAsync(int limit = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApprovalRecord>>(Array.Empty<ApprovalRecord>());

    public Task RevokeAsync(string signature, string revokedBy, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string> GenerateApprovalTokenAsync(string signature, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public Task<string?> ConsumeApprovalTokenAsync(string token, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
