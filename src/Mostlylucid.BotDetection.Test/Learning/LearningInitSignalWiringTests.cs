using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Learning;
using Mostlylucid.Ephemeral;
using StyloFlow.Orchestration;

namespace Mostlylucid.BotDetection.Test.Learning;

/// <summary>
///     Pins the wiring between the shared learning sink and StyloFlow's
///     <see cref="IInitSignalBus"/>: the first raise on
///     <see cref="TypedSignalSink{LearningEvent}"/> fires
///     <see cref="LearningSignalSinkOptions.InitSignal"/>, which the
///     bootstrap uses to lazy-construct the coordinator. This test doesn't
///     spin the whole BotDetectionModule -- it exercises just the wiring
///     shape that lives in the module so a future refactor cannot silently
///     defeat the lazy-boot property.
/// </summary>
public class LearningInitSignalWiringTests
{
    /// <summary>
    ///     Wires the same sink-factory shape the module uses. Kept in the
    ///     test so the "first raise fires init signal" behaviour is
    ///     exercised in isolation from BotDetection's much larger DI graph.
    /// </summary>
    private static IServiceCollection NewMinimalServices(
        LearningSignalSinkOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddOptions<LearningSignalSinkOptions>();
        if (options is not null)
        {
            services.Configure<LearningSignalSinkOptions>(o =>
            {
                o.Capacity = options.Capacity;
                o.Retention = options.Retention;
            });
        }
        services.AddInitSignalBus();
        services.AddSingleton<TypedSignalSink<LearningEvent>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LearningSignalSinkOptions>>().Value;
            var bus = sp.GetRequiredService<IInitSignalBus>();
            var inner = new SignalSink(opts.Capacity, opts.Retention);
            var sink = new TypedSignalSink<LearningEvent>(inner, maxCapacity: opts.Capacity, maxAge: opts.Retention);
            var initFired = 0;
            sink.TypedSignalRaised += _ =>
            {
                if (Interlocked.Exchange(ref initFired, 1) == 0)
                    bus.Raise(LearningSignalSinkOptions.InitSignal);
            };
            return sink;
        });
        return services;
    }

    private static void RaiseLearning(TypedSignalSink<LearningEvent> sink, LearningEventType type = LearningEventType.HighConfidenceDetection)
    {
        var evt = new LearningEvent
        {
            Type = type,
            Source = "test",
            RequestId = Guid.NewGuid().ToString("N"),
        };
        var key = LearningSignalKeys.For(type);
        sink.Raise(key.Name, evt, key: evt.RequestId);
    }

    [Fact]
    public void Init_signal_does_not_fire_before_any_raise()
    {
        var sp = NewMinimalServices().BuildServiceProvider();

        _ = sp.GetRequiredService<TypedSignalSink<LearningEvent>>();
        var bus = sp.GetRequiredService<IInitSignalBus>();

        bus.HasFired(LearningSignalSinkOptions.InitSignal).Should().BeFalse(
            "resolving the sink from DI must not fire the init signal -- only a real raise does");
    }

    [Fact]
    public void First_raise_fires_init_signal()
    {
        var sp = NewMinimalServices().BuildServiceProvider();
        var sink = sp.GetRequiredService<TypedSignalSink<LearningEvent>>();
        var bus = sp.GetRequiredService<IInitSignalBus>();

        RaiseLearning(sink);

        bus.HasFired(LearningSignalSinkOptions.InitSignal).Should().BeTrue();
    }

    [Fact]
    public void Subsequent_raises_do_not_re_fire_init_signal()
    {
        var sp = NewMinimalServices().BuildServiceProvider();
        var sink = sp.GetRequiredService<TypedSignalSink<LearningEvent>>();
        var bus = sp.GetRequiredService<IInitSignalBus>();

        var subscribeInvocations = 0;
        using var _ = bus.Subscribe(LearningSignalSinkOptions.InitSignal, () => subscribeInvocations++);

        RaiseLearning(sink);
        RaiseLearning(sink);
        RaiseLearning(sink);

        subscribeInvocations.Should().Be(1,
            "the sink's first-raise hook uses CompareExchange + the bus's Raise is idempotent -- " +
            "the handler must run exactly once regardless of how many events land");
    }

    [Fact]
    public async Task AddOnInitSignal_defers_coordinator_construction_until_first_raise()
    {
        var services = NewMinimalServices();
        // Register a probe coordinator via AddOnInitSignal so we can observe
        // when its ctor runs relative to the sink raise.
        services.AddOnInitSignal<ProbeCoordinator>(LearningSignalSinkOptions.InitSignal);
        var sp = services.BuildServiceProvider();

        // Start hosted services so the InitSignalBootstrap<ProbeCoordinator>
        // subscribes to the bus at Start.
        var hostedServices = sp.GetServices<IHostedService>().ToArray();
        foreach (var hs in hostedServices)
            await hs.StartAsync(CancellationToken.None);

        ProbeCoordinator.ConstructedCount.Should().Be(0,
            "no raise has happened; coordinator must remain dormant");

        var sink = sp.GetRequiredService<TypedSignalSink<LearningEvent>>();
        RaiseLearning(sink);

        ProbeCoordinator.ConstructedCount.Should().Be(1,
            "the first raise fires the init signal, which resolves the coordinator via DI");

        // A second raise must not re-construct.
        RaiseLearning(sink);
        ProbeCoordinator.ConstructedCount.Should().Be(1);

        foreach (var hs in hostedServices)
            await hs.StopAsync(CancellationToken.None);
    }

    private sealed class ProbeCoordinator
    {
        private static int s_constructedCount;
        public static int ConstructedCount => s_constructedCount;

        public ProbeCoordinator() => Interlocked.Increment(ref s_constructedCount);

        public static void Reset() => Interlocked.Exchange(ref s_constructedCount, 0);
    }

    public LearningInitSignalWiringTests()
    {
        ProbeCoordinator.Reset();
    }
}