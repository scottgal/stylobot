using Mostlylucid.BotDetection.Clustering;

namespace Mostlylucid.BotDetection.Test.Clustering;

public class LeidenClusteringTests
{
    [Fact]
    public void FindCommunities_DefaultResolution_MergesConnectedSimilarityGraph()
    {
        var adjacency = new Dictionary<int, List<(int Neighbor, double Weight)>>
        {
            [0] = [(1, 0.82), (2, 0.80)],
            [1] = [(0, 0.82), (2, 0.84)],
            [2] = [(0, 0.80), (1, 0.84)],
            [3] = [(4, 0.83), (5, 0.81)],
            [4] = [(3, 0.83), (5, 0.85)],
            [5] = [(3, 0.81), (4, 0.85)]
        };

        var labels = LeidenClustering.FindCommunities(adjacency, nodeCount: 6, resolution: 1.0);

        Assert.Equal(labels[0], labels[1]);
        Assert.Equal(labels[1], labels[2]);
        Assert.Equal(labels[3], labels[4]);
        Assert.Equal(labels[4], labels[5]);
        Assert.NotEqual(labels[0], labels[3]);
    }
}
