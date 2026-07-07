namespace Mostlylucid.BotDetection.Data.Centroids;

public abstract record CentroidWriteMessage
{
    public sealed record SessionCentroidWrite(SessionCentroidRow Row) : CentroidWriteMessage;

    public sealed record SignatureCentroidWrite(
        string SignatureId,
        float[] Vector,
        bool WasBot,
        double Confidence) : CentroidWriteMessage;

    public sealed record IntentCentroidWrite(
        string SignatureId,
        float[] Vector,
        double ThreatScore,
        string IntentCategory) : CentroidWriteMessage;
}
