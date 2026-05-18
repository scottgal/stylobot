using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.ThreatIntel;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

public class ThreatIntelLiveProviderBaseTests
{
    [Fact]
    public async Task RefreshAsync_CachesVerdict_SubsequentTryLookupHits()
    {
        var p = new TestProvider(quota: 10);
        var s = new ThreatSubject(ThreatSubjectType.Ip, "1.2.3.4");

        Assert.Null(p.TryLookup(s));            // cold
        await p.RefreshAsync(s, default);
        var v = p.TryLookup(s);
        Assert.NotNull(v);
        Assert.Equal("malicious", v!.Classification);
        Assert.Equal(1, p.FetchCalls);
    }

    [Fact]
    public async Task RefreshAsync_DedupesConcurrentCallsForSameSubject()
    {
        var p = new TestProvider(quota: 10) { FetchDelayMs = 60 };
        var s = new ThreatSubject(ThreatSubjectType.Ip, "1.2.3.4");

        // 20 callers race; only one underlying fetch should fire.
        var tasks = Enumerable.Range(0, 20).Select(_ => p.RefreshAsync(s, default)).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, p.FetchCalls);
        Assert.NotNull(p.TryLookup(s));
    }

    [Fact]
    public async Task RefreshAsync_QuotaExhausted_BlocksFurtherCalls()
    {
        var p = new TestProvider(quota: 3);

        await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Ip, "1.0.0.1"), default);
        await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Ip, "1.0.0.2"), default);
        await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Ip, "1.0.0.3"), default);
        // 4th attempt: quota exhausted, no fetch.
        await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Ip, "1.0.0.4"), default);
        await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Ip, "1.0.0.5"), default);

        Assert.Equal(3, p.FetchCalls);
    }

    [Fact]
    public async Task RefreshAsync_BreakerTripsAfterErrorRateThreshold()
    {
        // 5 attempts, all errors → 100% error rate, well over 20% trip threshold.
        var p = new TestProvider(quota: 100) { ThrowEveryFetch = true };
        for (var i = 0; i < 5; i++)
            await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Ip, $"1.0.0.{i}"), default);

        // 6th attempt: breaker should be open, fetch should NOT fire.
        var fetchesBeforeOpen = p.FetchCalls;
        await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Ip, "1.0.0.99"), default);
        Assert.Equal(fetchesBeforeOpen, p.FetchCalls);

        // GetStatus surfaces the open-until timestamp.
        var status = p.GetStatus();
        Assert.NotNull(status.BreakerOpenUntilUtc);
        Assert.True(status.BreakerOpenUntilUtc!.Value > DateTime.UtcNow);
    }

    [Fact]
    public async Task RefreshAsync_NullSubject_NoOp()
    {
        var p = new TestProvider(quota: 10);
        await p.RefreshAsync(subject: null, default);
        Assert.Equal(0, p.FetchCalls);
    }

    [Fact]
    public async Task RefreshAsync_UnsupportedSubjectType_NoOp()
    {
        // TestProvider only supports Ip
        var p = new TestProvider(quota: 10);
        await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Cve, "CVE-2021-44228"), default);
        Assert.Equal(0, p.FetchCalls);
    }

    [Fact]
    public async Task GetStatus_ReportsQuotaAndCacheSize()
    {
        var p = new TestProvider(quota: 5);
        await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Ip, "1.0.0.1"), default);
        await p.RefreshAsync(new ThreatSubject(ThreatSubjectType.Ip, "1.0.0.2"), default);

        var status = p.GetStatus();
        Assert.Equal("test", status.Provider);
        Assert.Equal(2, status.QuotaUsed);
        Assert.Equal(5, status.DailyQuota);
        Assert.Equal(2, status.CacheSize);
    }

    private sealed class TestProvider : ThreatIntelLiveProviderBase
    {
        public int FetchCalls;
        public int FetchDelayMs;
        public bool ThrowEveryFetch;

        public TestProvider(int quota) : base(new HttpClient(), NullLogger<TestProvider>.Instance)
        {
            DailyQuotaValue = quota;
        }

        private int DailyQuotaValue { get; }

        public override string Name => "test";
        public override IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; }
            = new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip };
        public override TimeSpan RefreshInterval => TimeSpan.FromMinutes(5);
        protected override int DailyQuota => DailyQuotaValue;
        protected override TimeSpan CacheTtl => TimeSpan.FromHours(1);

        protected override async Task<ThreatIntelVerdict?> FetchAsync(
            HttpClient http, ThreatSubject subject, CancellationToken ct)
        {
            Interlocked.Increment(ref FetchCalls);
            if (FetchDelayMs > 0) await Task.Delay(FetchDelayMs, ct);
            if (ThrowEveryFetch) throw new HttpRequestException("simulated vendor error");
            return new ThreatIntelVerdict
            {
                Provider = Name,
                Classification = "malicious",
                Confidence = 0.85,
                ObservedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddHours(1)
            };
        }
    }
}
