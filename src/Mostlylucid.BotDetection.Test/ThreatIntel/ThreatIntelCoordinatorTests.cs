using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.ThreatIntel;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

public class ThreatIntelCoordinatorTests
{
    private static ThreatIntelCoordinator Build(bool enabled, params IThreatIntelProvider[] providers)
    {
        var opts = Options.Create(new BotDetectionOptions
        {
            ThreatIntel = new ThreatIntelOptions { Enabled = enabled }
        });
        return new ThreatIntelCoordinator(opts, providers, NullLogger<ThreatIntelCoordinator>.Instance);
    }

    [Fact]
    public void IsEnabled_FalseWhenMasterSwitchOff()
    {
        var c = Build(enabled: false, new StubProvider("a", "malicious", 0.9));
        Assert.False(c.IsEnabled);
        Assert.Empty(c.Lookup(new ThreatSubject(ThreatSubjectType.Ip, "1.2.3.4")));
    }

    [Fact]
    public void IsEnabled_FalseWhenNoProviders()
    {
        var c = Build(enabled: true);
        Assert.False(c.IsEnabled);
    }

    [Fact]
    public void Lookup_ReturnsAllNonExpiredHits()
    {
        var p1 = new StubProvider("a", "malicious", 0.9);
        var p2 = new StubProvider("b", "tor", 0.8);
        var c = Build(enabled: true, p1, p2);

        var hits = c.Lookup(new ThreatSubject(ThreatSubjectType.Ip, "1.2.3.4"));
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void Lookup_FiltersPastExpiry()
    {
        var fresh = new StubProvider("a", "malicious", 0.9, expiresUtc: DateTime.UtcNow.AddMinutes(5));
        var stale = new StubProvider("b", "noise", 0.6, expiresUtc: DateTime.UtcNow.AddMinutes(-5));
        var c = Build(enabled: true, fresh, stale);

        var hits = c.Lookup(new ThreatSubject(ThreatSubjectType.Ip, "1.2.3.4"));
        Assert.Single(hits);
        Assert.Equal("a", hits[0].Provider);
    }

    [Fact]
    public void Lookup_SkipsProviderThatDoesntSupportSubjectType()
    {
        var ipOnly = new StubProvider("a", "malicious", 0.9);  // supports Ip
        var cveOnly = new StubProvider("b", "kev", 0.95) { Subjects = new HashSet<ThreatSubjectType> { ThreatSubjectType.Cve } };
        var c = Build(enabled: true, ipOnly, cveOnly);

        // IP subject hits only ipOnly
        var ipHits = c.Lookup(new ThreatSubject(ThreatSubjectType.Ip, "1.2.3.4"));
        Assert.Single(ipHits);
        Assert.Equal("a", ipHits[0].Provider);

        // CVE subject hits only cveOnly
        var cveHits = c.Lookup(new ThreatSubject(ThreatSubjectType.Cve, "CVE-2021-44228"));
        Assert.Single(cveHits);
        Assert.Equal("b", cveHits[0].Provider);
    }

    [Fact]
    public void Lookup_ThrowingProviderIsIsolated()
    {
        // A misbehaving provider must not take down the rest of the lookup.
        var broken = new StubProvider("a", "malicious", 0.9) { ThrowOnLookup = true };
        var working = new StubProvider("b", "noise", 0.5);
        var c = Build(enabled: true, broken, working);

        var hits = c.Lookup(new ThreatSubject(ThreatSubjectType.Ip, "1.2.3.4"));
        Assert.Single(hits);
        Assert.Equal("b", hits[0].Provider);
    }

    [Fact]
    public async Task EnrichAsync_HitsOnlyLiveProviders()
    {
        var offline = new StubProvider("off", "malicious", 0.9) { Mode = ThreatIntelMode.Offline };
        var live = new StubProvider("live", "scanner", 0.7) { Mode = ThreatIntelMode.Live };
        var c = Build(enabled: true, offline, live);

        await c.EnrichAsync(new ThreatSubject(ThreatSubjectType.Ip, "1.2.3.4"), default);

        Assert.Equal(0, offline.RefreshCalls);
        Assert.Equal(1, live.RefreshCalls);
    }

    private sealed class StubProvider : IThreatIntelProvider
    {
        private readonly ThreatIntelVerdict _verdict;

        public StubProvider(string name, string classification, double confidence,
            DateTime? expiresUtc = null)
        {
            Name = name;
            _verdict = new ThreatIntelVerdict
            {
                Provider = name,
                Classification = classification,
                Confidence = confidence,
                ObservedUtc = DateTime.UtcNow,
                ExpiresUtc = expiresUtc ?? default
            };
        }

        public string Name { get; }
        public ThreatIntelMode Mode { get; set; } = ThreatIntelMode.Offline;
        public IReadOnlySet<ThreatSubjectType> Subjects { get; set; }
            = new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip };
        public IReadOnlySet<ThreatSubjectType> SupportedSubjects => Subjects;
        public TimeSpan RefreshInterval => TimeSpan.FromHours(1);
        public bool ThrowOnLookup { get; set; }
        public int RefreshCalls;

        public ThreatIntelVerdict? TryLookup(ThreatSubject subject)
        {
            if (ThrowOnLookup) throw new InvalidOperationException("simulated provider failure");
            return _verdict;
        }

        public Task RefreshAsync(ThreatSubject? subject, CancellationToken ct)
        {
            Interlocked.Increment(ref RefreshCalls);
            return Task.CompletedTask;
        }
    }
}
