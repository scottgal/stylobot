using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over <c>/api/v1/approvals</c>. Write paths (Upsert / Revoke,
///     token generation/consumption) throw <see cref="NotSupportedException"/>; the
///     gateway that owns the approvals database is the only one that can write.
/// </summary>
internal sealed class RemoteFingerprintApprovalStore : IFingerprintApprovalStore
{
    private readonly GatewayApiClient _api;

    public RemoteFingerprintApprovalStore(GatewayApiClient api) => _api = api;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<ApprovalRecord?> GetAsync(string signature, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<ApprovalRecord>($"/api/v1/approvals/{Uri.EscapeDataString(signature)}", ct);

    public async Task<IReadOnlyList<ApprovalRecord>> ListRecentAsync(int limit = 50, CancellationToken ct = default)
        => await _api.GetEnvelopeListAsync<ApprovalRecord>($"/api/v1/approvals?limit={limit}", ct);

    public Task<ApprovalRecord> UpsertAsync(ApprovalRecord record, CancellationToken ct = default)
        => throw new NotSupportedException("Approval writes are not supported against a remote gateway.");

    public Task RevokeAsync(string signature, string revokedBy, CancellationToken ct = default)
        => throw new NotSupportedException("Approval writes are not supported against a remote gateway.");

    public Task<string> GenerateApprovalTokenAsync(string signature, CancellationToken ct = default)
        => throw new NotSupportedException("Approval token generation is not supported against a remote gateway.");

    public Task<string?> ConsumeApprovalTokenAsync(string token, CancellationToken ct = default)
        => throw new NotSupportedException("Approval token consumption is not supported against a remote gateway.");
}
