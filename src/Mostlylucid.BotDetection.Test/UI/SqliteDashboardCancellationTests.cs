using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Regression tests for the agent-found P1 bug (2026-06-22): three methods
///     on <c>SqliteDashboardEventStore</c> accepted <c>CancellationToken ct</c>
///     in their signature but forwarded a default-value token to
///     <c>EnsureInitializedAsync()</c>. Cancellation during the schema-init
///     window was silently lost; only the post-init <c>conn.OpenAsync(ct)</c>
///     hop honoured the token. Pre-cancelling the token now throws on the
///     first await regardless of init state.
///     <para>
///         The Postgres mirror routes <c>ct</c> through Dapper's
///         <c>CommandDefinition.cancellationToken</c>, so its behaviour is
///         covered by Dapper's own contract. These tests pin the SQLite path
///         because the bug was specific to its synchronous
///         <c>EnsureInitializedAsync</c> stub.
///     </para>
/// </summary>
public sealed class SqliteDashboardCancellationTests
{
    [Fact]
    public async Task GetEndpointStatsForSignatureAsync_propagates_a_precancelled_token()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("cancel-endpoint-sig");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fx.Store.GetEndpointStatsForSignatureAsync("sig-x", topN: 25, ct: cts.Token));
    }

    [Fact]
    public async Task GetHoneypotHitsAsync_propagates_a_precancelled_token()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("cancel-honeypot");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fx.Store.GetHoneypotHitsAsync(count: 50, ct: cts.Token));
    }

    [Fact]
    public async Task GetUserAgentVersionHistoryAsync_propagates_a_precancelled_token()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("cancel-ua-history");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fx.Store.GetUserAgentVersionHistoryAsync(family: "Chrome", hours: 24, ct: cts.Token));
    }
}
