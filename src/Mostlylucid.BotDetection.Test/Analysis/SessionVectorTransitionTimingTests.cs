using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Test.Analysis;

/// <summary>
///     Tests for the per-transition timing features added to SessionVectorizer (dims 126-128).
///     Validates impossible timing detection, timing consistency scoring, and fastest transition z-score.
/// </summary>
public class SessionVectorTransitionTimingTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 4, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Dimensions_Is129()
    {
        // 10*10 transitions + 10 stationary + 8 temporal + 8 fingerprint + 3 transition timing
        Assert.Equal(129, SessionVectorizer.Dimensions);
    }

    [Fact]
    public void Encode_WithNormalTiming_HasLowImpossibleRatio()
    {
        // Human-like timing: 500ms-2000ms between requests
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, BaseTime, "/", 200),
            new(RequestState.StaticAsset, BaseTime.AddMilliseconds(500), "/css/style.css", 200),
            new(RequestState.ApiCall, BaseTime.AddMilliseconds(1200), "/api/data", 200),
            new(RequestState.PageView, BaseTime.AddMilliseconds(2500), "/about", 200),
            new(RequestState.StaticAsset, BaseTime.AddMilliseconds(3200), "/js/app.js", 200)
        };

        var vector = SessionVectorizer.Encode(requests);

        // Dim 126: Impossible timing ratio should be 0 (all intervals > thresholds)
        Assert.Equal(0f, vector[126]);
    }

    [Fact]
    public void Encode_WithImpossibleTiming_HasHighImpossibleRatio()
    {
        // Bot-like: PageView->ApiCall in 5ms (impossible - page hasn't rendered)
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, BaseTime, "/", 200),
            new(RequestState.ApiCall, BaseTime.AddMilliseconds(5), "/api/data", 200),
            new(RequestState.PageView, BaseTime.AddMilliseconds(10), "/page2", 200),
            new(RequestState.ApiCall, BaseTime.AddMilliseconds(15), "/api/more", 200),
            new(RequestState.PageView, BaseTime.AddMilliseconds(20), "/page3", 200)
        };

        var vector = SessionVectorizer.Encode(requests);

        // Dim 126: Should be > 0 (some transitions are below impossible threshold)
        Assert.True(vector[126] > 0, $"Impossible timing ratio should be > 0, was {vector[126]}");
    }

    [Fact]
    public void Encode_WithConsistentTiming_HasHighConsistencyScore()
    {
        // Bot-like: exactly 100ms between every request (suspiciously consistent)
        var requests = new List<SessionRequest>();
        for (var i = 0; i < 10; i++)
            requests.Add(new SessionRequest(
                i % 2 == 0 ? RequestState.PageView : RequestState.ApiCall,
                BaseTime.AddMilliseconds(i * 100),
                $"/page{i}", 200));

        var vector = SessionVectorizer.Encode(requests);

        // Dim 127: Higher consistency score for bot-like timing (normalized vector, so absolute values shift)
        Assert.True(vector[127] > 0.2f, $"Timing consistency score should be > 0.2 for bot-like timing, was {vector[127]}");
    }

    [Fact]
    public void Encode_WithVariedTiming_HasLowConsistencyScore()
    {
        // Human-like: varied timing between requests
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, BaseTime, "/", 200),
            new(RequestState.StaticAsset, BaseTime.AddMilliseconds(300), "/css/a.css", 200),
            new(RequestState.ApiCall, BaseTime.AddMilliseconds(1500), "/api/x", 200),
            new(RequestState.PageView, BaseTime.AddMilliseconds(4200), "/about", 200),
            new(RequestState.FormSubmit, BaseTime.AddMilliseconds(9000), "/contact", 200),
            new(RequestState.PageView, BaseTime.AddMilliseconds(9800), "/thanks", 200)
        };

        var vector = SessionVectorizer.Encode(requests);

        // Dim 127: Lower consistency score for varied timing
        Assert.True(vector[127] < 0.7f, $"Timing consistency should be < 0.7 for human-like timing, was {vector[127]}");
    }

    [Fact]
    public void Encode_WithVeryFastTransition_HasHighFastestZScore()
    {
        // Bot: one transition at 5ms (way below 100ms baseline)
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, BaseTime, "/", 200),
            new(RequestState.ApiCall, BaseTime.AddMilliseconds(5), "/api/fast", 200),
            new(RequestState.PageView, BaseTime.AddMilliseconds(2000), "/slow", 200),
            new(RequestState.StaticAsset, BaseTime.AddMilliseconds(2500), "/img/x.png", 200)
        };

        var vector = SessionVectorizer.Encode(requests);

        // Dim 128: Positive z-score (fastest transition well below 100ms baseline, normalized)
        Assert.True(vector[128] > 0.2f, $"Fastest transition z-score should be > 0.2, was {vector[128]}");
    }

    [Fact]
    public void Encode_WithSlowTransitions_HasLowFastestZScore()
    {
        // Human: slowest transition is 500ms (well above 100ms baseline)
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, BaseTime, "/", 200),
            new(RequestState.StaticAsset, BaseTime.AddMilliseconds(500), "/css/x.css", 200),
            new(RequestState.PageView, BaseTime.AddMilliseconds(3000), "/page2", 200),
            new(RequestState.ApiCall, BaseTime.AddMilliseconds(5000), "/api/data", 200)
        };

        var vector = SessionVectorizer.Encode(requests);

        // Dim 128: Low z-score (fastest transition above baseline)
        Assert.Equal(0f, vector[128]);
    }

    [Fact]
    public void CosineSimilarity_DifferentDimensions_HandlesGracefully()
    {
        // Old 126-dim vector vs new 129-dim vector (migration scenario)
        var oldVector = new float[126];
        var newVector = new float[129];

        // Set some values
        oldVector[0] = 1f;
        newVector[0] = 1f;
        oldVector[100] = 0.5f;
        newVector[100] = 0.5f;

        var similarity = SessionVectorizer.CosineSimilarity(oldVector, newVector);

        // Should produce a valid similarity score, not 0 or crash
        Assert.True(similarity > 0, $"Cross-dimension similarity should be > 0, was {similarity}");
    }

    [Fact]
    public void CosineSimilarity_SameDimensions_ProducesCorrectResult()
    {
        var a = new float[129];
        var b = new float[129];
        a[0] = 1f; a[1] = 0f;
        b[0] = 1f; b[1] = 0f;

        Assert.Equal(1f, SessionVectorizer.CosineSimilarity(a, b), 0.001f);
    }

    [Fact]
    public void Encode_TooFewRequests_ReturnsZeroVector()
    {
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, BaseTime, "/", 200)
        };

        var vector = SessionVectorizer.Encode(requests);
        Assert.Equal(129, vector.Length);
        Assert.All(vector, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Encode_VectorIsNormalized()
    {
        var requests = new List<SessionRequest>
        {
            new(RequestState.PageView, BaseTime, "/", 200),
            new(RequestState.ApiCall, BaseTime.AddMilliseconds(500), "/api/x", 200),
            new(RequestState.PageView, BaseTime.AddMilliseconds(1500), "/page2", 200),
            new(RequestState.StaticAsset, BaseTime.AddMilliseconds(2000), "/css/x.css", 200)
        };

        var vector = SessionVectorizer.Encode(requests);

        // L2 norm should be ~1.0
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        Assert.InRange(norm, 0.99f, 1.01f);
    }
}
