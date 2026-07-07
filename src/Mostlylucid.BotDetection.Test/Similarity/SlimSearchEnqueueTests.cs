using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Centroids;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Test.Similarity;

/// <summary>
///     Pins the enqueue-time sampling contract for all three Slim* AddAsync paths
///     introduced in Task 4 of the centroid drain fix.
///
///     Each search class delegates SQLite persistence to <see cref="ICentroidWriter.Enqueue"/>
///     (LFU-sampled, synchronous, non-blocking) instead of a fire-and-forget Task.Run.
///
///     Necessity thresholds used:
///     -- SlimSignature + SlimSession:  default SamplingThreshold=0.05 / DecisionThreshold=0.70
///        HIGH: borderline bot (probability near 0.70)  -> necessity near 1.0
///        LOW:  confident-not-bot (probability=0.01, wasBot=false) -> necessity ~6e-10
///     -- SlimIntent: botProbability is fixed at 0.0 so Uncertainty(0.0, 0.70) ~3e-10 (negligible);
///        necessity reduces to threat * recency. Default SamplingThreshold=0.05 is sufficient.
///        DecisionNecessity.Value(0.0, 0.0, 0, 0.70, 604800) ~0 < 0.05  (NOT enqueued).
///        DecisionNecessity.Value(0.0, 0.9, 0, 0.70, 604800) ~0.9 >= 0.05 (enqueued).
/// </summary>
public class SlimSearchEnqueueTests
{
    // -----------------------------------------------------------------------
    // Shared fake writer
    // -----------------------------------------------------------------------

    private sealed class FakeCentroidWriter : ICentroidWriter
    {
        private readonly List<CentroidWriteMessage> _captured = [];
        private int _callerThreadId = -1;

        public void Enqueue(CentroidWriteMessage message)
        {
            _callerThreadId = Environment.CurrentManagedThreadId;
            _captured.Add(message);
        }

        public IReadOnlyList<CentroidWriteMessage> Captured => _captured;
        public int CallerThreadId => _callerThreadId;

        public int QueueDepth => 0;
        public long DroppedCount => 0;
    }

    private static IOptions<CentroidWriterOptions> DefaultOpts() =>
        Options.Create(new CentroidWriterOptions());

    private static IOptions<CentroidWriterOptions> HighThresholdOpts(double samplingThreshold = 0.25) =>
        Options.Create(new CentroidWriterOptions { SamplingThreshold = samplingThreshold });

    // -----------------------------------------------------------------------
    // SlimSignatureSimilaritySearch
    // -----------------------------------------------------------------------

    private static SlimSignatureSimilaritySearch BuildSig(FakeCentroidWriter writer,
        IOptions<CentroidWriterOptions>? centroidOpts = null)
    {
        var opts = Options.Create(new BotDetectionOptions { SelfMaintenance = { SignatureCacheSize = 100 } });
        return new SlimSignatureSimilaritySearch(
            opts,
            writer,
            centroidOpts ?? DefaultOpts(),
            NullLogger<SlimSignatureSimilaritySearch>.Instance);
    }

    [Fact]
    public async Task Signature_HighNecessity_EnqueuesSignatureCentroidWrite()
    {
        // HIGH: wasBot=true, confidence=0.72 (borderline at threshold 0.70)
        // DecisionNecessity.Value(0.72, 0.72, 0, 0.70, 604800) ~ 0.9951 >= 0.05
        var writer = new FakeCentroidWriter();
        var sut = BuildSig(writer);
        var vector = new float[] { 1f, 0f, 0f };

        await sut.AddAsync(vector, "sig-high", wasBot: true, confidence: 0.72);

        Assert.Single(writer.Captured);
        var msg = Assert.IsType<CentroidWriteMessage.SignatureCentroidWrite>(writer.Captured[0]);
        Assert.Equal("sig-high", msg.SignatureId);
        Assert.True(msg.WasBot);
        Assert.Equal(0.72, msg.Confidence);
    }

    [Fact]
    public async Task Signature_LowNecessity_DoesNotEnqueue()
    {
        // LOW: wasBot=false, confidence=0.01
        // DecisionNecessity.Value(0.01, 0.0, 0, 0.70, 604800) ~ 6e-10 < 0.05 (sampled out)
        var writer = new FakeCentroidWriter();
        var sut = BuildSig(writer);
        var vector = new float[] { 0f, 1f, 0f };

        await sut.AddAsync(vector, "sig-low", wasBot: false, confidence: 0.01);

        Assert.Empty(writer.Captured);
    }

    [Fact]
    public async Task Signature_AddAsync_EnqueuesOnCallingThread()
    {
        // Verify no Task.Run: Enqueue must execute on the same thread as the caller.
        // await Task.CompletedTask keeps the continuation on the calling thread in the
        // xUnit sync context; a Task.Run persist would have captured a different thread id.
        var writer = new FakeCentroidWriter();
        var sut = BuildSig(writer);
        var vector = new float[] { 1f, 0f, 0f };

        var callerThread = Environment.CurrentManagedThreadId;
        await sut.AddAsync(vector, "sig-thread", wasBot: true, confidence: 0.72);

        Assert.Single(writer.Captured);
        Assert.Equal(callerThread, writer.CallerThreadId);
    }

    // -----------------------------------------------------------------------
    // SlimIntentSearch
    // -----------------------------------------------------------------------

    private static SlimIntentSearch BuildIntent(FakeCentroidWriter writer,
        IOptions<CentroidWriterOptions>? centroidOpts = null)
    {
        var opts = Options.Create(new BotDetectionOptions { SelfMaintenance = { IntentCacheSize = 100 } });
        return new SlimIntentSearch(
            opts,
            writer,
            centroidOpts ?? DefaultOpts(),
            NullLogger<SlimIntentSearch>.Instance);
    }

    [Fact]
    public async Task Intent_HighNecessity_EnqueuesIntentCentroidWrite()
    {
        // HIGH: threatScore=0.9 with default opts (SamplingThreshold=0.05)
        // DecisionNecessity.Value(0.0, 0.9, 0, 0.70, 604800) ~0.9 >= 0.05
        var writer = new FakeCentroidWriter();
        var sut = BuildIntent(writer);
        var vector = new float[] { 1f, 0f, 0f };

        await sut.AddAsync(vector, "intent-high", threatScore: 0.9, intentCategory: "attacking");

        Assert.Single(writer.Captured);
        var msg = Assert.IsType<CentroidWriteMessage.IntentCentroidWrite>(writer.Captured[0]);
        Assert.Equal("intent-high", msg.SignatureId);
        Assert.Equal(0.9, msg.ThreatScore);
        Assert.Equal("attacking", msg.IntentCategory);
    }

    [Fact]
    public async Task Intent_LowNecessity_DoesNotEnqueue()
    {
        // LOW: threatScore=0.0 with default opts (SamplingThreshold=0.05)
        // DecisionNecessity.Value(0.0, 0.0, 0, 0.70, 604800) ~0 < 0.05 (sampled out)
        // botProbability=0.0 makes Uncertainty negligible (~3e-10); necessity reduces to
        // threat * recency = 0.0 * 1.0 = 0 -- no raised threshold workaround needed.
        var writer = new FakeCentroidWriter();
        var sut = BuildIntent(writer);
        var vector = new float[] { 0f, 1f, 0f };

        await sut.AddAsync(vector, "intent-low", threatScore: 0.0, intentCategory: "browsing");

        Assert.Empty(writer.Captured);
    }

    [Fact]
    public async Task Intent_AddAsync_EnqueuesOnCallingThread()
    {
        var writer = new FakeCentroidWriter();
        var sut = BuildIntent(writer);
        var vector = new float[] { 1f, 0f, 0f };

        var callerThread = Environment.CurrentManagedThreadId;
        await sut.AddAsync(vector, "intent-thread", threatScore: 0.9, intentCategory: "attacking");

        Assert.Single(writer.Captured);
        Assert.Equal(callerThread, writer.CallerThreadId);
    }

    // -----------------------------------------------------------------------
    // SlimSessionVectorSearch
    // -----------------------------------------------------------------------

    private static readonly int Dims = SessionVectorizer.Dimensions;

    private static float[] MakeSessionVector(int hotIndex = 0)
    {
        var v = new float[Dims];
        v[hotIndex] = 1f;
        return v;
    }

    private static SlimSessionVectorSearch BuildSession(FakeCentroidWriter writer,
        IOptions<CentroidWriterOptions>? centroidOpts = null)
    {
        var opts = Options.Create(new BotDetectionOptions { SelfMaintenance = { SessionCacheSize = 100 } });
        return new SlimSessionVectorSearch(
            opts,
            writer,
            centroidOpts ?? DefaultOpts(),
            NullLogger<SlimSessionVectorSearch>.Instance);
    }

    [Fact]
    public async Task Session_HighNecessity_EnqueuesSessionCentroidWrite()
    {
        // HIGH: isBot=true, botProbability=0.72 (borderline at threshold 0.70)
        // DecisionNecessity.Value(0.72, 0.72, 0, 0.70, 604800) ~ 0.9951 >= 0.05
        var writer = new FakeCentroidWriter();
        var sut = BuildSession(writer);
        var vector = MakeSessionVector(0);

        await sut.AddAsync(vector, "session-high", isBot: true, botProbability: 0.72);

        Assert.Single(writer.Captured);
        var msg = Assert.IsType<CentroidWriteMessage.SessionCentroidWrite>(writer.Captured[0]);
        Assert.Equal("session-high", msg.Row.SignatureId);
        Assert.True(msg.Row.IsBot);
        Assert.Equal(0.72, msg.Row.BotProbability);
    }

    [Fact]
    public async Task Session_LowNecessity_DoesNotEnqueue()
    {
        // LOW: isBot=false, botProbability=0.01
        // DecisionNecessity.Value(0.01, 0.0, 0, 0.70, 604800) ~ 6e-10 < 0.05 (sampled out)
        var writer = new FakeCentroidWriter();
        var sut = BuildSession(writer);
        var vector = MakeSessionVector(1);

        await sut.AddAsync(vector, "session-low", isBot: false, botProbability: 0.01);

        Assert.Empty(writer.Captured);
    }

    [Fact]
    public async Task Session_AddAsync_EnqueuesOnCallingThread()
    {
        var writer = new FakeCentroidWriter();
        var sut = BuildSession(writer);
        var vector = MakeSessionVector(2);

        var callerThread = Environment.CurrentManagedThreadId;
        await sut.AddAsync(vector, "session-thread", isBot: true, botProbability: 0.72);

        Assert.Single(writer.Captured);
        Assert.Equal(callerThread, writer.CallerThreadId);
    }
}
