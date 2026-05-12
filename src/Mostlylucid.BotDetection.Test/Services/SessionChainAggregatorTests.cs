using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class SessionChainAggregatorTests
{
    [Fact]
    public void AggregateFromTransitions_Empty_ReturnsEmpty()
    {
        var chain = SessionChainAggregator.AggregateFromTransitions(
            Array.Empty<SessionTransitionData>(),
            chainLength: 5,
            minTotalTransitions: 1);
        Assert.Empty(chain);
    }

    [Fact]
    public void AggregateFromTransitions_GreedyWalkFromMostCommonStart()
    {
        // 3 sessions all dominated by PageView; transitions: PageView->StaticAsset (heavy),
        // StaticAsset->ApiCall (medium), ApiCall->ApiCall (light)
        var transitions = new Dictionary<(RequestState From, RequestState To), int>
        {
            { (RequestState.PageView, RequestState.StaticAsset), 30 },
            { (RequestState.StaticAsset, RequestState.ApiCall), 10 },
            { (RequestState.ApiCall, RequestState.ApiCall), 5 },
            { (RequestState.ApiCall, RequestState.SignalR), 3 }
        };

        var sessions = new[]
        {
            new SessionTransitionData(RequestState.PageView, transitions),
            new SessionTransitionData(RequestState.PageView, transitions),
            new SessionTransitionData(RequestState.PageView, transitions),
        };

        var chain = SessionChainAggregator.AggregateFromTransitions(sessions, chainLength: 4, minTotalTransitions: 10);

        // Expected greedy walk: start at PageView (modal), then StaticAsset (top from PageView),
        // then ApiCall (top from StaticAsset), then ApiCall again (top from ApiCall is the self-loop)
        Assert.Equal(4, chain.Length);
        Assert.Equal(RequestState.PageView, chain[0]);
        Assert.Equal(RequestState.StaticAsset, chain[1]);
        Assert.Equal(RequestState.ApiCall, chain[2]);
        Assert.Equal(RequestState.ApiCall, chain[3]);
    }

    [Fact]
    public void AggregateFromTransitions_NoOutboundFromCurrent_TruncatesWalk()
    {
        var transitions = new Dictionary<(RequestState From, RequestState To), int>
        {
            { (RequestState.PageView, RequestState.NotFound), 10 }
        };
        var sessions = new[]
        {
            new SessionTransitionData(RequestState.PageView, transitions),
            new SessionTransitionData(RequestState.PageView, transitions),
        };

        // After NotFound there are no outbound transitions; walk stops there.
        var chain = SessionChainAggregator.AggregateFromTransitions(sessions, chainLength: 5, minTotalTransitions: 1);
        Assert.Equal(2, chain.Length);
        Assert.Equal(RequestState.PageView, chain[0]);
        Assert.Equal(RequestState.NotFound, chain[1]);
    }

    [Fact]
    public void AggregateFromTransitions_BelowMinTotal_ReturnsEmpty()
    {
        var transitions = new Dictionary<(RequestState From, RequestState To), int>
        {
            { (RequestState.PageView, RequestState.ApiCall), 1 }
        };
        var sessions = new[] { new SessionTransitionData(RequestState.PageView, transitions) };
        var chain = SessionChainAggregator.AggregateFromTransitions(sessions, chainLength: 5, minTotalTransitions: 100);
        Assert.Empty(chain);
    }

    [Fact]
    public void AggregateFromTransitions_NoDominantState_ReturnsEmpty()
    {
        var transitions = new Dictionary<(RequestState From, RequestState To), int>
        {
            { (RequestState.PageView, RequestState.ApiCall), 10 }
        };
        var sessions = new[] { new SessionTransitionData(null, transitions) };
        var chain = SessionChainAggregator.AggregateFromTransitions(sessions, chainLength: 5, minTotalTransitions: 1);
        Assert.Empty(chain);
    }

    [Fact]
    public void ParseTransitionCounts_HandlesValidShape()
    {
        var json = """{"PageView->StaticAsset":5,"StaticAsset->ApiCall":3}""";
        var counts = SessionChainAggregator.ParseTransitionCounts(json);
        Assert.Equal(5, counts[(RequestState.PageView, RequestState.StaticAsset)]);
        Assert.Equal(3, counts[(RequestState.StaticAsset, RequestState.ApiCall)]);
    }

    [Fact]
    public void ParseTransitionCounts_SkipsUnknownStates()
    {
        var json = """{"Bogus->StaticAsset":5,"PageView->Bogus":3,"PageView->StaticAsset":7}""";
        var counts = SessionChainAggregator.ParseTransitionCounts(json);
        Assert.Single(counts);
        Assert.Equal(7, counts[(RequestState.PageView, RequestState.StaticAsset)]);
    }

    [Fact]
    public void ParseTransitionCounts_HandlesEmptyOrInvalid()
    {
        Assert.Empty(SessionChainAggregator.ParseTransitionCounts(""));
        Assert.Empty(SessionChainAggregator.ParseTransitionCounts("not json"));
        Assert.Empty(SessionChainAggregator.ParseTransitionCounts("{}"));
    }

    [Fact]
    public void ParseDominantState_KnownState_Returns()
    {
        Assert.Equal(RequestState.PageView, SessionChainAggregator.ParseDominantState("PageView"));
        Assert.Equal(RequestState.ApiCall, SessionChainAggregator.ParseDominantState("apicall"));
    }

    [Fact]
    public void ParseDominantState_UnknownOrNull_ReturnsNull()
    {
        Assert.Null(SessionChainAggregator.ParseDominantState(null));
        Assert.Null(SessionChainAggregator.ParseDominantState(""));
        Assert.Null(SessionChainAggregator.ParseDominantState("Bogus"));
    }
}
