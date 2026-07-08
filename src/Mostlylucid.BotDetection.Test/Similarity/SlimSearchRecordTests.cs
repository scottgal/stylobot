using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Similarity;

/// <summary>
///     Pins the Record hot-path contract for all three Slim* AddAsync paths
///     introduced in Task B of the centroid-persistence rework.
///
///     Each search class now delegates SQLite persistence to the store's
///     RecordX(...)  method (non-blocking, synchronous, on calling thread)
///     instead of the deleted ICentroidWriter channel.
///
///     Tests verify:
///     (a) RecordX is called synchronously ON THE CALLING THREAD (no Task.Run).
///     (b) The payload matches the AddAsync arguments.
///     (c) AddAsync returns a completed Task (never blocks).
/// </summary>
public class SlimSearchRecordTests
{
    // -----------------------------------------------------------------------
    // Capturing fakes
    // -----------------------------------------------------------------------

    private sealed class CapturingSignatureStore : ISignatureCentroidStore
    {
        public int RecordThreadId = -1;
        public string? SignatureId;
        public bool WasBot;
        public double Confidence;
        public int CallCount;

        public void RecordSignature(string signatureId, float[] vector, bool wasBot, double confidence)
        {
            RecordThreadId = Environment.CurrentManagedThreadId;
            SignatureId    = signatureId;
            WasBot         = wasBot;
            Confidence     = confidence;
            CallCount++;
        }

        public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SignatureCentroidRow>>([]);
        public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CapturingSessionStore : ISessionCentroidStore
    {
        public int RecordThreadId = -1;
        public string? SignatureId;
        public int CallCount;

        public void RecordSession(SessionCentroidRow row)
        {
            RecordThreadId = Environment.CurrentManagedThreadId;
            SignatureId    = row.SignatureId;
            CallCount++;
        }

        public Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCentroidRow>>([]);
        public Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CapturingIntentStore : IIntentCentroidStore
    {
        public int RecordThreadId = -1;
        public string? SignatureId;
        public double ThreatScore;
        public string? Category;
        public int CallCount;

        public void RecordIntent(string signatureId, float[] vector, double threatScore, string category)
        {
            RecordThreadId = Environment.CurrentManagedThreadId;
            SignatureId    = signatureId;
            ThreatScore    = threatScore;
            Category       = category;
            CallCount++;
        }

        public Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IntentCentroidRow>>([]);
        public Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // SlimSignatureSimilaritySearch
    // -----------------------------------------------------------------------

    private static SlimSignatureSimilaritySearch BuildSig(ISignatureCentroidStore store)
    {
        var opts = Options.Create(new BotDetectionOptions { SelfMaintenance = { SignatureCacheSize = 100 } });
        return new SlimSignatureSimilaritySearch(opts, store, NullLogger<SlimSignatureSimilaritySearch>.Instance);
    }

    [Fact]
    public async Task Signature_AddAsync_CallsRecordSignature_OnCallingThread()
    {
        var store = new CapturingSignatureStore();
        var sut   = BuildSig(store);
        var vector = new float[] { 1f, 0f, 0f };

        var callerThread = Environment.CurrentManagedThreadId;
        await sut.AddAsync(vector, "sig-1", wasBot: true, confidence: 0.72);

        Assert.Equal(1, store.CallCount);
        Assert.Equal(callerThread, store.RecordThreadId);
    }

    [Fact]
    public async Task Signature_AddAsync_PassesCorrectPayload()
    {
        var store = new CapturingSignatureStore();
        var sut   = BuildSig(store);
        var vector = new float[] { 1f, 0f, 0f };

        await sut.AddAsync(vector, "sig-payload", wasBot: true, confidence: 0.88);

        Assert.Equal("sig-payload", store.SignatureId);
        Assert.True(store.WasBot);
        Assert.Equal(0.88, store.Confidence);
    }

    [Fact]
    public async Task Signature_AddAsync_ReturnsCompletedTask()
    {
        var store = new CapturingSignatureStore();
        var sut   = BuildSig(store);
        var vector = new float[] { 0f, 1f, 0f };

        var task = sut.AddAsync(vector, "sig-completed", wasBot: false, confidence: 0.1);

        Assert.True(task.IsCompleted, "AddAsync must return a completed Task (no blocking await)");
        await task; // ensure no exception
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

    private static SlimSessionVectorSearch BuildSession(ISessionCentroidStore store)
    {
        var opts = Options.Create(new BotDetectionOptions { SelfMaintenance = { SessionCacheSize = 100 } });
        return new SlimSessionVectorSearch(opts, store, NullLogger<SlimSessionVectorSearch>.Instance);
    }

    [Fact]
    public async Task Session_AddAsync_CallsRecordSession_OnCallingThread()
    {
        var store = new CapturingSessionStore();
        var sut   = BuildSession(store);
        var vector = MakeSessionVector(0);

        var callerThread = Environment.CurrentManagedThreadId;
        await sut.AddAsync(vector, "sess-1", isBot: true, botProbability: 0.72);

        Assert.Equal(1, store.CallCount);
        Assert.Equal(callerThread, store.RecordThreadId);
    }

    [Fact]
    public async Task Session_AddAsync_PassesCorrectSignatureId()
    {
        var store = new CapturingSessionStore();
        var sut   = BuildSession(store);
        var vector = MakeSessionVector(1);

        await sut.AddAsync(vector, "sess-payload", isBot: false, botProbability: 0.2);

        Assert.Equal("sess-payload", store.SignatureId);
    }

    [Fact]
    public async Task Session_AddAsync_ReturnsCompletedTask()
    {
        var store = new CapturingSessionStore();
        var sut   = BuildSession(store);
        var vector = MakeSessionVector(2);

        var task = sut.AddAsync(vector, "sess-completed", isBot: false, botProbability: 0.1);

        Assert.True(task.IsCompleted, "AddAsync must return a completed Task (no blocking await)");
        await task;
    }

    // -----------------------------------------------------------------------
    // SlimIntentSearch
    // -----------------------------------------------------------------------

    private static SlimIntentSearch BuildIntent(IIntentCentroidStore store)
    {
        var opts = Options.Create(new BotDetectionOptions { SelfMaintenance = { IntentCacheSize = 100 } });
        return new SlimIntentSearch(opts, store, NullLogger<SlimIntentSearch>.Instance);
    }

    [Fact]
    public async Task Intent_AddAsync_CallsRecordIntent_OnCallingThread()
    {
        var store = new CapturingIntentStore();
        var sut   = BuildIntent(store);
        var vector = new float[] { 1f, 0f, 0f };

        var callerThread = Environment.CurrentManagedThreadId;
        await sut.AddAsync(vector, "intent-1", threatScore: 0.9, intentCategory: "scanning");

        Assert.Equal(1, store.CallCount);
        Assert.Equal(callerThread, store.RecordThreadId);
    }

    [Fact]
    public async Task Intent_AddAsync_PassesCorrectPayload()
    {
        var store = new CapturingIntentStore();
        var sut   = BuildIntent(store);
        var vector = new float[] { 1f, 0f, 0f };

        await sut.AddAsync(vector, "intent-payload", threatScore: 0.75, intentCategory: "attacking");

        Assert.Equal("intent-payload", store.SignatureId);
        Assert.Equal(0.75, store.ThreatScore);
        Assert.Equal("attacking", store.Category);
    }

    [Fact]
    public async Task Intent_AddAsync_ReturnsCompletedTask()
    {
        var store = new CapturingIntentStore();
        var sut   = BuildIntent(store);
        var vector = new float[] { 0f, 1f, 0f };

        var task = sut.AddAsync(vector, "intent-completed", threatScore: 0.0, intentCategory: "browsing");

        Assert.True(task.IsCompleted, "AddAsync must return a completed Task (no blocking await)");
        await task;
    }
}
