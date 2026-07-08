using System.Runtime.InteropServices;

namespace Mostlylucid.BotDetection.Data;

public sealed record SignatureCentroidRow(
    string SignatureId, float[] Vector, bool WasBot, double Confidence);

public sealed class SessionCentroidRow
{
    public string SignatureId       { get; init; } = "";
    public float[] Vector           { get; init; } = [];
    public float[]? VelocityVector  { get; init; }
    public float[]? VarianceVector  { get; init; }
    public float[]? FreqFingerprint { get; init; }
    public string? ClusterId        { get; init; }
    public int CompressionLevel     { get; init; }
    public bool IsBot               { get; init; }
    public double BotProbability    { get; init; }
    public double Priority          { get; init; }
}

public sealed record IntentCentroidRow(
    string SignatureId, float[] Vector, double ThreatScore, string IntentCategory);

internal static class CentroidFloatPacker
{
    internal static byte[] Pack(float[] v) =>
        MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    internal static float[] Unpack(byte[] b)
    {
        var result = new float[b.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(b).CopyTo(result);
        return result;
    }
}
