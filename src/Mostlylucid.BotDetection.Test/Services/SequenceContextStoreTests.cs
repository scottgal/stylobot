using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class SequenceContextStoreTests
{
    [Fact]
    public void GetOrCreate_new_signature_creates_context_at_position_zero()
    {
        var store = new SequenceContextStore();
        var ctx = store.GetOrCreate("sig1");
        Assert.Equal(0, ctx.Position);
        Assert.False(ctx.HasDiverged);
        Assert.Equal(CentroidType.Unknown, ctx.CentroidType);
    }

    [Fact]
    public void GetOrCreate_same_signature_returns_same_context()
    {
        var store = new SequenceContextStore();
        var ctx1 = store.GetOrCreate("sig1");
        var ctx2 = store.GetOrCreate("sig1");
        Assert.Equal(ctx1.ChainId, ctx2.ChainId);
    }

    [Fact]
    public void TryGet_unknown_signature_returns_null()
    {
        var store = new SequenceContextStore();
        var result = store.TryGet("unknown");
        Assert.Null(result);
    }

    [Fact]
    public void Update_stores_new_version()
    {
        var store = new SequenceContextStore();
        var ctx = store.GetOrCreate("sig1");
        var updated = ctx with { Position = 2, HasDiverged = true };
        store.Update("sig1", updated);
        var retrieved = store.TryGet("sig1");
        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved.Position);
        Assert.True(retrieved.HasDiverged);
    }

    [Fact]
    public void Expire_removes_stale_entries()
    {
        var store = new SequenceContextStore();
        var ctx = store.GetOrCreate("sig1");
        var stale = ctx with { LastRequest = DateTimeOffset.UtcNow.AddMinutes(-40) };
        store.Update("sig1", stale);
        store.EvictExpired(TimeSpan.FromMinutes(30));
        Assert.Null(store.TryGet("sig1"));
    }

    [Fact]
    public void Expire_keeps_fresh_entries()
    {
        var store = new SequenceContextStore();
        store.GetOrCreate("sig1");
        store.EvictExpired(TimeSpan.FromMinutes(30));
        Assert.NotNull(store.TryGet("sig1"));
    }

    [Fact]
    public void ExpiredContext_GetOrCreate_returns_new_chain_id()
    {
        var store = new SequenceContextStore();
        var ctx = store.GetOrCreate("sig1");
        var originalChainId = ctx.ChainId;
        var stale = ctx with { LastRequest = DateTimeOffset.UtcNow.AddMinutes(-40) };
        store.Update("sig1", stale);

        var renewed = store.GetOrCreate("sig1", sessionGapMinutes: 30);
        Assert.NotEqual(originalChainId, renewed.ChainId);
        Assert.Equal(0, renewed.Position);
    }

    [Fact]
    public void Count_returns_number_of_contexts()
    {
        var store = new SequenceContextStore();
        store.GetOrCreate("sig1");
        store.GetOrCreate("sig2");
        Assert.Equal(2, store.Count);
    }
}
