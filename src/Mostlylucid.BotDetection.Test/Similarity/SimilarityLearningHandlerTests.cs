using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Similarity;

public class SimilarityLearningHandlerTests
{
    [Fact]
    public void HandledEventTypes_DoesNotContainFullDetection()
    {
        var handler = new SimilarityLearningHandler(
            new FeatureVectorizer(),
            new CapturingSimilaritySearch(),
            NullLogger<SimilarityLearningHandler>.Instance);

        Assert.DoesNotContain(LearningEventType.FullDetection, handler.HandledEventTypes);
    }

    [Fact]
    public void HandledEventTypes_ContainsHighConfidenceDetection()
    {
        var handler = new SimilarityLearningHandler(
            new FeatureVectorizer(),
            new CapturingSimilaritySearch(),
            NullLogger<SimilarityLearningHandler>.Instance);

        Assert.Contains(LearningEventType.HighConfidenceDetection, handler.HandledEventTypes);
    }

    [Fact]
    public async Task HandleAsync_UsesPrimarySignature_WhenPresent()
    {
        var search = new CapturingSimilaritySearch();
        var handler = new SimilarityLearningHandler(
            new FeatureVectorizer(),
            search,
            NullLogger<SimilarityLearningHandler>.Instance);

        await handler.HandleAsync(new LearningEvent
        {
            Type = LearningEventType.HighConfidenceDetection,
            Source = "test",
            RequestId = "request-id",
            Features = new Dictionary<string, double>
            {
                ["req:ua_length"] = 12,
                ["ua:contains_bot"] = 1
            },
            Label = true,
            Confidence = 0.91,
            Metadata = new Dictionary<string, object>
            {
                ["primarySignature"] = "stable-signature"
            }
        });

        Assert.Equal("stable-signature", search.LastSignatureId);
        Assert.True(search.LastWasBot);
        Assert.Equal(0.91, search.LastConfidence);
    }

    private sealed class CapturingSimilaritySearch : ISignatureSimilaritySearch
    {
        public string? LastSignatureId { get; private set; }
        public bool LastWasBot { get; private set; }
        public double LastConfidence { get; private set; }
        public int Count { get; private set; }

        public Task<IReadOnlyList<SimilarSignature>> FindSimilarAsync(
            float[] vector,
            int topK = 5,
            float minSimilarity = 0.80f,
            string? embeddingContext = null)
            => Task.FromResult<IReadOnlyList<SimilarSignature>>([]);

        public Task AddAsync(
            float[] vector,
            string signatureId,
            bool wasBot,
            double confidence,
            string? embeddingContext = null)
        {
            LastSignatureId = signatureId;
            LastWasBot = wasBot;
            LastConfidence = confidence;
            Count++;
            return Task.CompletedTask;
        }

        public Task SaveAsync() => Task.CompletedTask;

        public Task LoadAsync() => Task.CompletedTask;
    }
}
