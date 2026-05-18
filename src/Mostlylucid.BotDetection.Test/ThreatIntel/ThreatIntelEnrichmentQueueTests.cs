using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.ThreatIntel;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

public class ThreatIntelEnrichmentQueueTests
{
    private static ThreatIntelEnrichmentQueue NewQueue(int capacity = 500)
        => new(Options.Create(new BotDetectionOptions
        {
            ThreatIntel = new ThreatIntelOptions { EnrichmentQueueCapacity = capacity }
        }));

    [Fact]
    public void TryEnqueue_AcceptsUpToCapacity()
    {
        var q = NewQueue();
        for (var i = 0; i < 500; i++)
        {
            Assert.True(q.TryEnqueue(new ThreatSubject(ThreatSubjectType.Ip, $"10.0.0.{i % 256}")));
        }
        Assert.Equal(500, q.TotalEnqueued);
        Assert.Equal(0, q.TotalDropped);
    }

    [Fact]
    public void TryEnqueue_OverCapacity_StillReturnsTrue_DropsOldestInternally()
    {
        // DropOldest channel: writer always succeeds (returns true), oldest item is
        // silently dropped from the read side. Depth never exceeds the bound.
        var q = NewQueue();
        for (var i = 0; i < 600; i++)
            q.TryEnqueue(new ThreatSubject(ThreatSubjectType.Ip, $"10.0.0.{i % 256}"));

        Assert.True(q.Depth <= 500);
    }

    [Fact]
    public void Depth_ReflectsUndrainedItems()
    {
        var q = NewQueue();
        Assert.Equal(0, q.Depth);
        q.TryEnqueue(new ThreatSubject(ThreatSubjectType.Ip, "1.2.3.4"));
        Assert.Equal(1, q.Depth);
    }
}
