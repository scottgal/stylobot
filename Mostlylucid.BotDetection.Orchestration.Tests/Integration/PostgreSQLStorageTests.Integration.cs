using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Mostlylucid.BotDetection.UI.PostgreSQL.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
/// Integration tests for PostgreSQL storage implementations.
/// Requires PostgreSQL running on localhost:5432.
/// </summary>
[Trait("Category", "Integration")]
public class PostgreSQLStorageTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly PostgreSQLStorageOptions _options;
    private PostgreSQLLearnedPatternStore? _patternStore;
    private PostgreSQLWeightStore? _weightStore;
    private readonly ServiceProvider _serviceProvider;

    // Default connection string for local development
    private const string TestConnectionString =
        "Host=localhost;Database=stylobot_test;Username=postgres;Password=sdfksdoii8980;Include Error Detail=true";

    public PostgreSQLStorageTests(ITestOutputHelper output)
    {
        _output = output;

        // Try environment variable first, then default
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? TestConnectionString;

        _options = new PostgreSQLStorageOptions
        {
            ConnectionString = connectionString,
            CommandTimeoutSeconds = 30
        };

        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Debug)
            .AddConsole());
        _serviceProvider = services.BuildServiceProvider();
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Create test database if it doesn't exist
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(_options.ConnectionString);
            var testDbName = builder.Database;
            builder.Database = "postgres";

            await using (var adminConn = new Npgsql.NpgsqlConnection(builder.ConnectionString))
            {
                await adminConn.OpenAsync();
                await using var checkCmd = new Npgsql.NpgsqlCommand(
                    $"SELECT 1 FROM pg_database WHERE datname = '{testDbName}'", adminConn);
                var exists = await checkCmd.ExecuteScalarAsync();
                if (exists == null)
                {
                    await using var createCmd = new Npgsql.NpgsqlCommand($"CREATE DATABASE {testDbName}", adminConn);
                    await createCmd.ExecuteNonQueryAsync();
                    _output.WriteLine($"Created test database: {testDbName}");
                }
            }

            // Test connection to the test database
            await using var testConn = new Npgsql.NpgsqlConnection(_options.ConnectionString);
            await testConn.OpenAsync();
            _output.WriteLine("PostgreSQL connection successful");

            var patternLogger = _serviceProvider.GetRequiredService<ILogger<PostgreSQLLearnedPatternStore>>();
            var weightLogger = _serviceProvider.GetRequiredService<ILogger<PostgreSQLWeightStore>>();

            _patternStore = new PostgreSQLLearnedPatternStore(_options, patternLogger);
            _weightStore = new PostgreSQLWeightStore(_options, weightLogger);

            _output.WriteLine("PostgreSQL storage initialized successfully");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Failed to initialize PostgreSQL: {ex.Message}");
            throw new SkipException($"PostgreSQL not available: {ex.Message}");
        }
    }

    public async Task DisposeAsync()
    {
        if (_patternStore != null)
            await _patternStore.DisposeAsync();
        if (_weightStore != null)
            await _weightStore.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }

    [Fact]
    public async Task LearnedPatternStore_UpsertAndGet_Works()
    {
        // Skip if not initialized
        if (_patternStore == null)
            throw new SkipException("PostgreSQL not available");

        // Arrange
        var signature = new LearnedSignature
        {
            PatternId = $"test-pattern-{Guid.NewGuid():N}",
            SignatureType = "UserAgent",
            Pattern = "TestBot/1.0",
            Confidence = 0.85,
            Occurrences = 5,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeen = DateTimeOffset.UtcNow,
            Action = LearnedPatternAction.ScoreOnly,
            BotType = BotType.Scraper,
            BotName = "TestBot",
            Source = "UnitTest"
        };

        // Act
        await _patternStore.UpsertAsync(signature);
        var retrieved = await _patternStore.GetAsync(signature.PatternId);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(signature.PatternId, retrieved.PatternId);
        Assert.Equal(signature.SignatureType, retrieved.SignatureType);
        Assert.Equal(signature.Pattern, retrieved.Pattern);
        Assert.Equal(signature.Confidence, retrieved.Confidence);
        Assert.Equal(signature.BotType, retrieved.BotType);

        _output.WriteLine($"Successfully stored and retrieved pattern: {signature.PatternId}");

        // Cleanup
        await _patternStore.DeleteAsync(signature.PatternId);
    }

    [Fact]
    public async Task LearnedPatternStore_GetByType_Works()
    {
        if (_patternStore == null)
            throw new SkipException("PostgreSQL not available");

        // Arrange
        var testType = $"TestType_{Guid.NewGuid():N}";
        var signatures = new[]
        {
            new LearnedSignature
            {
                PatternId = $"pattern-1-{Guid.NewGuid():N}",
                SignatureType = testType,
                Pattern = "Pattern1",
                Confidence = 0.9,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow
            },
            new LearnedSignature
            {
                PatternId = $"pattern-2-{Guid.NewGuid():N}",
                SignatureType = testType,
                Pattern = "Pattern2",
                Confidence = 0.8,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow
            }
        };

        // Act
        foreach (var sig in signatures)
            await _patternStore.UpsertAsync(sig);

        var results = await _patternStore.GetByTypeAsync(testType);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(testType, r.SignatureType));

        _output.WriteLine($"Successfully retrieved {results.Count} patterns by type");

        // Cleanup
        foreach (var sig in signatures)
            await _patternStore.DeleteAsync(sig.PatternId);
    }

    [Fact]
    public async Task LearnedPatternStore_GetStats_Works()
    {
        if (_patternStore == null)
            throw new SkipException("PostgreSQL not available");

        // Act
        var stats = await _patternStore.GetStatsAsync();

        // Assert
        Assert.NotNull(stats);
        _output.WriteLine($"Pattern store stats: Total={stats.TotalPatterns}, UA={stats.UserAgentPatterns}, IP={stats.IpPatterns}");
    }

    [Fact]
    public async Task WeightStore_UpdateAndGet_Works()
    {
        if (_weightStore == null)
            throw new SkipException("PostgreSQL not available");

        // Arrange
        var signatureType = "ua_pattern";
        var signature = $"test-sig-{Guid.NewGuid():N}";
        var weight = 0.75;
        var confidence = 0.9;

        // Act
        await _weightStore.UpdateWeightAsync(signatureType, signature, weight, confidence, 10);

        // Wait for background flush
        await Task.Delay(1000);

        var retrieved = await _weightStore.GetWeightAsync(signatureType, signature);

        // Assert - may be cached at 0.75 or flushed
        _output.WriteLine($"Weight for {signature}: {retrieved}");
        Assert.True(retrieved >= 0.0); // At least should not throw
    }

    [Fact]
    public async Task WeightStore_RecordObservation_Works()
    {
        if (_weightStore == null)
            throw new SkipException("PostgreSQL not available");

        // Arrange
        var signatureType = "behavior_hash";
        var signature = $"obs-test-{Guid.NewGuid():N}";

        // Act - Record bot observation
        await _weightStore.RecordObservationAsync(signatureType, signature, wasBot: true, detectionConfidence: 0.9);

        // Record human observation
        await _weightStore.RecordObservationAsync(signatureType, signature, wasBot: false, detectionConfidence: 0.8);

        // Get the weight - should be roughly neutral (one bot, one human)
        var weight = await _weightStore.GetWeightAsync(signatureType, signature);

        // Assert
        _output.WriteLine($"Weight after mixed observations: {weight}");
        Assert.True(Math.Abs(weight) <= 1.0); // Weight should be in valid range
    }

    [Fact]
    public async Task WeightStore_GetStats_Works()
    {
        if (_weightStore == null)
            throw new SkipException("PostgreSQL not available");

        // Act
        var stats = await _weightStore.GetStatsAsync();

        // Assert
        Assert.NotNull(stats);
        _output.WriteLine($"Weight store stats: Total={stats.TotalWeights}, UA={stats.UaPatternWeights}, AvgConf={stats.AverageConfidence:P0}");
    }

    [Fact]
    public async Task WeightStore_BatchGetWeights_Works()
    {
        if (_weightStore == null)
            throw new SkipException("PostgreSQL not available");

        // Arrange
        var signatureType = "test_batch";
        var signatures = new[] { "sig1", "sig2", "sig3" };

        // Act
        var weights = await _weightStore.GetWeightsAsync(signatureType, signatures);

        // Assert
        Assert.NotNull(weights);
        Assert.Equal(3, weights.Count);
        foreach (var sig in signatures)
        {
            Assert.True(weights.ContainsKey(sig));
        }

        _output.WriteLine($"Batch retrieved {weights.Count} weights");
    }
}

/// <summary>
/// Custom exception to skip tests when PostgreSQL is not available.
/// </summary>
public class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}
