using Mostlylucid.BotDetection.Events;

namespace Mostlylucid.BotDetection.Test.Events;

public class LearningEventBusTests
{
    [Fact]
    public async Task Subscribe_ReceivesCopy_WithoutConsumingPrimaryReader()
    {
        var bus = new LearningEventBus(capacity: 10);
        var subscriber = bus.Subscribe("audit", capacity: 10);
        var evt = new LearningEvent
        {
            Type = LearningEventType.HighConfidenceDetection,
            Source = "test",
            RequestId = "req-1"
        };

        Assert.True(bus.TryPublish(evt));

        var primaryRead = await bus.Reader.ReadAsync();
        var subscriberRead = await subscriber.ReadAsync();

        Assert.Equal("req-1", primaryRead.RequestId);
        Assert.Equal("req-1", subscriberRead.RequestId);
    }
}
