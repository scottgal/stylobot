using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Models;

/// <summary>
///     ONE key for the bot/human cut. <see cref="ClassificationOptions.BotFloor"/> is the value of
///     record; the obsolete <c>BotDetection:BotThreshold</c> <em>derives</em> from it, so
///     "counted as a bot" and "acted on as a bot" cannot disagree. The point of these tests is
///     that divergence is IMPOSSIBLE, not documented: an operator who sets the obsolete key to a
///     different value gets BotFloor's number and a loud boot warning naming the winner.
/// </summary>
public sealed class BotFloorSingleKeyTests
{
    [Fact]
    public void Obsolete_threshold_reads_through_to_BotFloor()
    {
        var options = new BotDetectionOptions
        {
            Classification = new ClassificationOptions { BotFloor = 0.75 }
        };

#pragma warning disable CS0618 // the obsolete key is exactly what is under test
        options.BotThreshold.Should().Be(0.75,
            "the obsolete key derives from BotFloor, so one number answers both questions");
#pragma warning restore CS0618
    }

    [Fact]
    public void Explicitly_set_obsolete_threshold_cannot_win()
    {
        var options = new BotDetectionOptions
        {
            Classification = new ClassificationOptions { BotFloor = 0.75 }
        };

#pragma warning disable CS0618
        options.BotThreshold = 0.90;
        options.BotThreshold.Should().Be(0.75, "BotFloor is the survivor; the explicit value must not create a second answer");
#pragma warning restore CS0618

        options.ConfiguredBotThreshold.Should().Be(0.90, "the raw configured value is retained so the boot check can report it");
    }

    [Fact]
    public void Configured_value_is_null_when_the_obsolete_key_was_never_set()
    {
        new BotDetectionOptions().ConfiguredBotThreshold.Should().BeNull();
    }

    [Fact]
    public async Task Boot_warning_names_both_values_and_the_winner()
    {
        var options = new BotDetectionOptions
        {
            Classification = new ClassificationOptions { BotFloor = 0.75 }
        };
#pragma warning disable CS0618
        options.BotThreshold = 0.90;
#pragma warning restore CS0618

        var logger = new CapturingLogger<BotThresholdDivergenceWarningService>();
        await new BotThresholdDivergenceWarningService(Options.Create(options), logger)
            .StartAsync(CancellationToken.None);

        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("0.75", "the winning BotFloor value is stated");
        warning.Message.Should().Contain("0.90", "the ignored explicit value is stated");
        warning.Message.Should().Contain("BotFloor", "the operator is told which key won");
    }

    [Fact]
    public async Task Boot_warning_is_silent_when_the_keys_agree()
    {
        var options = new BotDetectionOptions
        {
            Classification = new ClassificationOptions { BotFloor = 0.70 }
        };
#pragma warning disable CS0618
        options.BotThreshold = 0.70; // explicitly set, but to the same number
#pragma warning restore CS0618

        var logger = new CapturingLogger<BotThresholdDivergenceWarningService>();
        await new BotThresholdDivergenceWarningService(Options.Create(options), logger)
            .StartAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Validator_still_rejects_an_out_of_range_explicit_value()
    {
        var options = new BotDetectionOptions { MaxRequestsPerMinute = 60 };
#pragma warning disable CS0618
        options.BotThreshold = 1.5; // the raw configured value is what gets range-checked
#pragma warning restore CS0618

        var result = new BotDetectionOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue("an explicit garbage value must still be an error, not silently ignored");
        result.Failures.Should().Contain(f => f.Contains("BotThreshold"));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
