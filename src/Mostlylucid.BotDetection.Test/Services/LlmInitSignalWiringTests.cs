using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using StyloFlow.Orchestration;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Pins the wiring between the shared LLM request sink and
///     <see cref="IInitSignalBus"/>: the first raise on
///     <see cref="TypedSignalSink{LlmClassificationRequest}"/> fires
///     <see cref="LlmClassificationSinkOptions.InitSignal"/>, which
///     lazy-boots the coordinator via <c>AddOnInitSignal&lt;T&gt;</c>. Kept
///     isolated from the coordinator's LLM-service side because that path
///     depends on multiple external services out of scope for wiring tests.
/// </summary>
public class LlmInitSignalWiringTests
{
    private static IServiceCollection NewMinimalServices(
        LlmClassificationSinkOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddOptions<LlmClassificationSinkOptions>();
        if (options is not null)
        {
            services.Configure<LlmClassificationSinkOptions>(o =>
            {
                o.Capacity = options.Capacity;
                o.Retention = options.Retention;
            });
        }
        services.AddInitSignalBus();
        services.AddSingleton<TypedSignalSink<LlmClassificationRequest>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmClassificationSinkOptions>>().Value;
            var bus = sp.GetRequiredService<IInitSignalBus>();
            var inner = new SignalSink(opts.Capacity, opts.Retention);
            var sink = new TypedSignalSink<LlmClassificationRequest>(
                inner, maxCapacity: opts.Capacity, maxAge: opts.Retention);
            var initFired = 0;
            sink.TypedSignalRaised += _ =>
            {
                if (Interlocked.Exchange(ref initFired, 1) == 0)
                    bus.Raise(LlmClassificationSinkOptions.InitSignal);
            };
            return sink;
        });
        return services;
    }

    private static LlmClassificationRequest NewRequest(string requestId = "req-1")
        => new()
        {
            RequestId = requestId,
            PrimarySignature = "sig-1",
            UserAgent = "test-ua/1.0",
            PreBuiltRequestInfo = "GET /",
            HeuristicProbability = 0.5,
            TopReasons = new List<string>(),
            Signals = new Dictionary<string, object>(),
        };

    private static void Raise(TypedSignalSink<LlmClassificationRequest> sink, LlmClassificationRequest request)
    {
        sink.Raise(LlmClassificationCoordinator.RequestSignal.Name, request, key: request.RequestId);
    }

    [Fact]
    public void Init_signal_does_not_fire_before_any_raise()
    {
        var sp = NewMinimalServices().BuildServiceProvider();

        _ = sp.GetRequiredService<TypedSignalSink<LlmClassificationRequest>>();
        var bus = sp.GetRequiredService<IInitSignalBus>();

        bus.HasFired(LlmClassificationSinkOptions.InitSignal).Should().BeFalse(
            "resolving the sink from DI must not fire the init signal -- only a real raise does");
    }

    [Fact]
    public void First_raise_fires_init_signal()
    {
        var sp = NewMinimalServices().BuildServiceProvider();
        var sink = sp.GetRequiredService<TypedSignalSink<LlmClassificationRequest>>();
        var bus = sp.GetRequiredService<IInitSignalBus>();

        Raise(sink, NewRequest());

        bus.HasFired(LlmClassificationSinkOptions.InitSignal).Should().BeTrue();
    }

    [Fact]
    public void Subsequent_raises_do_not_re_fire_init_signal()
    {
        var sp = NewMinimalServices().BuildServiceProvider();
        var sink = sp.GetRequiredService<TypedSignalSink<LlmClassificationRequest>>();
        var bus = sp.GetRequiredService<IInitSignalBus>();

        var subscribeInvocations = 0;
        using var _ = bus.Subscribe(LlmClassificationSinkOptions.InitSignal, () => subscribeInvocations++);

        Raise(sink, NewRequest("req-1"));
        Raise(sink, NewRequest("req-2"));
        Raise(sink, NewRequest("req-3"));

        subscribeInvocations.Should().Be(1);
    }

    [Fact]
    public async Task AddOnInitSignal_defers_probe_construction_until_first_raise()
    {
        var services = NewMinimalServices();
        services.AddOnInitSignal<ProbeCoordinator>(LlmClassificationSinkOptions.InitSignal);
        var sp = services.BuildServiceProvider();

        var hostedServices = sp.GetServices<IHostedService>().ToArray();
        foreach (var hs in hostedServices)
            await hs.StartAsync(CancellationToken.None);

        ProbeCoordinator.ConstructedCount.Should().Be(0);

        var sink = sp.GetRequiredService<TypedSignalSink<LlmClassificationRequest>>();
        Raise(sink, NewRequest());

        ProbeCoordinator.ConstructedCount.Should().Be(1);

        Raise(sink, NewRequest("req-2"));
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

    public LlmInitSignalWiringTests()
    {
        ProbeCoordinator.Reset();
    }
}
