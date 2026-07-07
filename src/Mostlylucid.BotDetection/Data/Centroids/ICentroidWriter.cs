namespace Mostlylucid.BotDetection.Data.Centroids;

public interface ICentroidWriter
{
    void Enqueue(CentroidWriteMessage message);

    int QueueDepth { get; }

    long DroppedCount { get; }
}
