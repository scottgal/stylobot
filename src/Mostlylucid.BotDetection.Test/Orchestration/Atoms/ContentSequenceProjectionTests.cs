using FluentAssertions;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Orchestration.Atoms;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms;

public class ContentSequenceProjectionTests
{
    [Fact]
    public void DocumentMarker_ProjectsOnlyTheMostRecentDocumentSegment()
    {
        var now = DateTimeOffset.UtcNow;
        var requests = new[]
        {
            Request(RequestState.PageView, now, "/first", document: true),
            Request(RequestState.ApiCall, now.AddSeconds(1), "/first/api"),
            Request(RequestState.PageView, now.AddSeconds(2), "/second", document: true),
            Request(RequestState.StaticAsset, now.AddSeconds(3), "/second/app.js"),
        };

        var projection = Project(requests);

        projection.Projected.Should().BeTrue();
        projection.DocumentIndex.Should().Be(2);
        projection.ContentPath.Should().Be("/second");
        projection.Position.Should().Be(2);
    }

    [Fact]
    public void PageViewWithoutDocumentMarker_DoesNotCreateSequenceBoundary()
    {
        var now = DateTimeOffset.UtcNow;

        var projection = Project(new[]
        {
            Request(RequestState.PageView, now, "/not-a-navigation"),
            Request(RequestState.ApiCall, now.AddSeconds(1), "/api"),
        });

        projection.Projected.Should().BeFalse();
    }

    [Fact]
    public void Projection_CapsTheDocumentSegmentAtTwentyRequests()
    {
        var now = DateTimeOffset.UtcNow;
        var requests = new List<SessionRequest>
        {
            Request(RequestState.PageView, now, "/document", document: true),
        };
        for (var i = 1; i <= 25; i++)
            requests.Add(Request(RequestState.ApiCall, now.AddSeconds(i), $"/api/{i}"));

        var projection = Project(requests);

        projection.Projected.Should().BeTrue();
        projection.Position.Should().Be(20);
        projection.WindowRequestCount.Should().Be(20);
    }

    [Fact]
    public void Projection_ResetsItsDerivedWindowAfterSixtySecondsIdle()
    {
        var now = DateTimeOffset.UtcNow;

        var projection = Project(new[]
        {
            Request(RequestState.PageView, now, "/document", document: true),
            Request(RequestState.ApiCall, now.AddSeconds(1), "/api/first"),
            Request(RequestState.ApiCall, now.AddSeconds(62), "/api/after-idle"),
        });

        projection.Projected.Should().BeTrue();
        projection.WindowRequestCount.Should().Be(1);
    }

    [Fact]
    public void Projection_IsPureAndLeavesNoSequenceContextOwnerUnderRotation()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10_000; i++)
        {
            var projection = Project(new[]
            {
                Request(RequestState.PageView, now, $"/document/{i}", document: true),
                Request(RequestState.ApiCall, now.AddSeconds(1), $"/api/{i}"),
            });
            projection.Projected.Should().BeTrue();
        }

        typeof(ContentSequenceAtom).Assembly
            .GetType("Mostlylucid.BotDetection.Services.SequenceContextStore")
            .Should().BeNull();
    }

    private static SessionRequest Request(RequestState state, DateTimeOffset timestamp, string path, bool document = false)
        => new(state, timestamp, path, 200, IsDocumentNavigation: document);

    private static Projection Project(IReadOnlyList<SessionRequest> requests)
    {
        var projected = ContentSequenceAtom.TryProjectSegment(requests, out var segment);
        if (!projected) return new Projection(false, -1, string.Empty, 0, 0);

        return new Projection(
            true,
            segment.DocumentIndex,
            segment.ContentPath,
            segment.Position,
            segment.WindowRequests.Length);
    }

    private sealed record Projection(
        bool Projected,
        int DocumentIndex,
        string ContentPath,
        int Position,
        int WindowRequestCount);
}
