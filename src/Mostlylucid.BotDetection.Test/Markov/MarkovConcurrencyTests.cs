using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Markov;

/// <summary>
///     Concurrency and stress tests for MarkovTracker and supporting types.
///     Verifies thread safety under contention and memory bounds under load.
/// </summary>
public class MarkovConcurrencyTests
{
    #region MarkovTracker Concurrent Access

    [Fact]
    public async Task RecordTransition_ConcurrentSameSignature_NoExceptions()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            try
            {
                for (var j = 0; j < 100; j++)
                {
                    tracker.RecordTransition("shared-sig", $"/page{j % 10}",
                        now.AddSeconds(i * 100 + j), false, false, true);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
        var stats = tracker.GetStats();
        Assert.Equal(1, stats.ActiveSignatures);
    }

    [Fact]
    public async Task RecordTransition_ConcurrentDifferentSignatures_NoExceptions()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            try
            {
                for (var j = 0; j < 50; j++)
                {
                    tracker.RecordTransition($"sig-{i}", $"/page{j % 8}",
                        now.AddSeconds(j), i % 2 == 0, i % 3 == 0, true);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
        var stats = tracker.GetStats();
        Assert.Equal(50, stats.ActiveSignatures);
    }

    [Fact]
    public async Task RecordTransition_WithConcurrentFlush_NoExceptions()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Writer tasks
        var writers = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            try
            {
                for (var j = 0; j < 200; j++)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    tracker.RecordTransition($"sig-{i}", $"/page{j % 5}",
                        now.AddSeconds(j), false, false, true);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        // Flush task (runs concurrently)
        var flusher = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 50; i++)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    tracker.FlushCohortUpdates();
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        await Task.WhenAll(writers.Append(flusher));

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task GetDriftSignals_ConcurrentWithRecordTransition_NoExceptions()
    {
        var tracker = CreateTracker(minTransitionsForDrift: 3);
        var now = DateTime.UtcNow;
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Pre-warm with enough transitions for drift
        for (var i = 0; i < 20; i++)
            tracker.RecordTransition("target-sig", $"/page{i % 5}", now.AddSeconds(i), false, false, true);
        tracker.FlushCohortUpdates();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Writers
        var writer = Task.Run(() =>
        {
            try
            {
                var j = 20;
                while (!cts.Token.IsCancellationRequested)
                {
                    tracker.RecordTransition("target-sig", $"/page{j % 5}",
                        now.AddSeconds(j++), false, false, true);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Readers
        var readers = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var drift = tracker.GetDriftSignals("target-sig", false, true);
                    // Just verify no crash; values may vary
                    Assert.True(drift.SelfDrift >= 0 || drift == DriftSignals.Empty);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(readers.Append(writer));

        Assert.Empty(exceptions);
    }

    #endregion

    #region DecayingTransitionMatrix Concurrent Access

    [Fact]
    public async Task TransitionMatrix_ConcurrentRecordAndRead_NoExceptions()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var writers = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            try
            {
                var j = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    matrix.RecordTransition($"/page{j % 10}", $"/page{(j + 1) % 10}", DateTime.UtcNow);
                    j++;
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        var readers = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    matrix.GetTransitionProbability("/page0", "/page1", DateTime.UtcNow);
                    matrix.GetDistribution("/page0", DateTime.UtcNow);
                    matrix.GetPathEntropy(DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        Assert.Empty(exceptions);
        Assert.True(matrix.TotalTransitions > 0);
    }

    #endregion

    #region Memory Bounds and Stress

    [Fact]
    public void MarkovTracker_ManySignatures_HandlesGracefully()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;

        // Simulate 1000 unique signatures with minimal transitions
        for (var i = 0; i < 1000; i++)
        {
            tracker.RecordTransition($"sig-{i}", "/home", now, false, false, true);
            tracker.RecordTransition($"sig-{i}", "/about", now.AddSeconds(1), false, false, true);
        }

        var stats = tracker.GetStats();
        Assert.Equal(1000, stats.ActiveSignatures);
    }

    [Fact]
    public void DecayingTransitionMatrix_MaxEdgesPruning_BoundsMemory()
    {
        // Create matrix with low MaxEdgesPerNode to force pruning
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1), maxK: 5);
        var now = DateTime.UtcNow;

        // Add many edges from a single source node
        for (var i = 0; i < 50; i++)
        {
            matrix.RecordTransition("/source", $"/target-{i}", now.AddSeconds(i));
        }

        // Distribution should be bounded (pruning triggers at 2*maxK, prunes to maxK)
        var dist = matrix.GetDistribution("/source", now);
        Assert.True(dist.Count <= 10,
            $"Expected at most 2*maxK=10 edges (bounded by pruning), got {dist.Count}");
    }

    [Fact]
    public void RecentTransitionBuffer_OverflowWraps_NoDataLoss()
    {
        var buffer = new RecentTransitionBuffer(10);

        // Add 25 transitions (wraps around 2.5 times)
        for (var i = 0; i < 25; i++)
            buffer.Add(($"/from{i}", $"/to{i}"));

        var recent = buffer.GetRecent();
        Assert.Equal(10, recent.Count);

        // Should contain the last 10 transitions (15-24)
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal($"/from{15 + i}", recent[i].From);
            Assert.Equal($"/to{15 + i}", recent[i].To);
        }
    }

    [Fact]
    public void PathNormalizer_StressWithManyPatterns_Consistent()
    {
        // Verify PathNormalizer produces consistent results under volume
        var paths = new List<string>();
        for (var i = 0; i < 1000; i++)
        {
            paths.Add($"/product/{i}");
            paths.Add($"/api/v2/users/{Guid.NewGuid()}");
            paths.Add($"/blog/post-title-{i}?page={i % 10}&sort=date");
        }

        var results = new Dictionary<string, string>();
        foreach (var path in paths)
        {
            var normalized = PathNormalizer.Normalize(path);
            if (results.TryGetValue(path, out var previous))
                Assert.Equal(previous, normalized); // Same input → same output
            results[path] = normalized;
        }

        // All /product/{id} paths should normalize to the same thing
        var productPaths = paths.Where(p => p.StartsWith("/product/"))
            .Select(PathNormalizer.Normalize)
            .Distinct()
            .ToList();
        Assert.Single(productPaths);
    }

    [Fact]
    public void DivergenceMetrics_LargeDistributions_Performance()
    {
        // Build large distributions (simulating many unique paths)
        var random = new Random(42);
        var distP = new Dictionary<string, double>();
        var distQ = new Dictionary<string, double>();

        for (var i = 0; i < 500; i++)
        {
            var key = $"/path{i}";
            distP[key] = random.NextDouble();
            distQ[key] = random.NextDouble();
        }

        // Normalize
        var sumP = distP.Values.Sum();
        var sumQ = distQ.Values.Sum();
        foreach (var k in distP.Keys.ToList()) distP[k] /= sumP;
        foreach (var k in distQ.Keys.ToList()) distQ[k] /= sumQ;

        // Should complete quickly and produce valid result
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var jsd = DivergenceMetrics.JensenShannonDivergence(distP, distQ);
        sw.Stop();

        Assert.InRange(jsd, 0.0, 1.0);
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"JSD on 500-key distributions took {sw.ElapsedMilliseconds}ms (expected <100ms)");
    }

    #endregion

    #region Edge Cases Under Contention

    [Fact]
    public async Task FlushCohortUpdates_ConcurrentMultipleFlusher_NoDoubleCounting()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;

        // Record transitions that generate cohort updates
        for (var i = 0; i < 100; i++)
        {
            tracker.RecordTransition("sig1", $"/page{i % 5}", now.AddSeconds(i), false, false, true);
        }

        // Multiple flushers racing
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            try
            {
                tracker.FlushCohortUpdates();
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);

        // After all flushers complete, pending queue should be empty
        var stats = tracker.GetStats();
        Assert.Equal(0, stats.PendingCohortUpdates);
    }

    [Fact]
    public async Task RecordTransition_HighContention_DriftSignalsRemainBounded()
    {
        var tracker = CreateTracker(minTransitionsForDrift: 3);
        var now = DateTime.UtcNow;
        var drifts = new System.Collections.Concurrent.ConcurrentBag<DriftSignals>();

        // Pre-warm
        for (var i = 0; i < 10; i++)
            tracker.RecordTransition("contended-sig", $"/page{i % 3}", now.AddSeconds(i), false, false, true);
        tracker.FlushCohortUpdates();

        // Concurrent writes + drift collection
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 50; j++)
            {
                var drift = tracker.RecordTransition("contended-sig", $"/page{j % 5}",
                    now.AddSeconds(1000 + i * 50 + j), false, false, true);
                if (drift != DriftSignals.Empty)
                    drifts.Add(drift);
            }
        }));

        await Task.WhenAll(tasks);

        // All drift values should be bounded
        foreach (var drift in drifts)
        {
            Assert.InRange(drift.SelfDrift, 0.0, 1.0);
            Assert.InRange(drift.HumanDrift, 0.0, 1.0);
            Assert.InRange(drift.TransitionNovelty, 0.0, 1.0);
            Assert.InRange(drift.LoopScore, 0.0, 1.0);
            Assert.True(drift.SequenceSurprise >= 0);
        }
    }

    #endregion

    #region Helpers

    private static MarkovTracker CreateTracker(int minTransitionsForDrift = 5)
    {
        var options = new BotDetectionOptions
        {
            Markov = new MarkovOptions
            {
                MinTransitionsForDrift = minTransitionsForDrift,
                SignatureHalfLifeHours = 1,
                CohortHalfLifeHours = 6,
                GlobalHalfLifeHours = 24,
                MaxEdgesPerNode = 20,
                RecentTransitionWindowSize = 30
            }
        };

        return new MarkovTracker(
            NullLogger<MarkovTracker>.Instance,
            Options.Create(options));
    }

    #endregion
}
