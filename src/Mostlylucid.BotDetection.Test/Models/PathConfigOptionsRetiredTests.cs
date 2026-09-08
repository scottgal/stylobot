using System.Reflection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.HealthEndpoints;
using FluentAssertions;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Models;

/// <summary>
///     Records the deliberate state of the three path-config options and pins the
///     replacement mechanism, so the retirement is a decision rather than an accident.
///     <para>
///         HISTORY: <c>ExcludedPaths</c>, <c>SignatureOnlyPaths</c> and <c>PathOverrides</c>
///         were LIVE until the v8 atom refactor (<c>1a8d2745</c>, 2026-07-05) deleted the
///         old contributor <c>BotDetectionMiddleware</c> that read them; the atom
///         middleware that took the name reads no options at all. They are deliberately
///         NOT re-wired: every one of them is a detection skip or an enforcement bypass,
///         which the product's hard rules forbid ("NEVER skip detection. No skip paths, no
///         logonly workarounds."). Probe traffic is recognised in-pipeline instead —
///         <see cref="HealthEndpoints"/> + a trusted-internal source classifies it
///         <c>BotType.Internal</c> (counted and displayed, never throttled).
///     </para>
///     <para>
///         If a future change wants these to do something, it must delete the matching
///         <see cref="ObsoleteAttribute"/> assertion below and explain in review why a
///         skip path is the right answer — the test exists to force that conversation.
///     </para>
/// </summary>
public sealed class PathConfigOptionsRetiredTests
{
    public static TheoryData<string> RetiredOptions =>
    [
        nameof(BotDetectionOptions.ExcludedPaths),
        nameof(BotDetectionOptions.SignatureOnlyPaths),
        nameof(BotDetectionOptions.PathOverrides),
    ];

    [Theory]
    [MemberData(nameof(RetiredOptions))]
    public void Retired_path_option_is_marked_obsolete_with_its_replacement_named(string propertyName)
    {
        var property = typeof(BotDetectionOptions)
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        property.Should().NotBeNull($"{propertyName} must stay on the type so host config keeps binding");

        var obsolete = property!.GetCustomAttribute<ObsoleteAttribute>();
        obsolete.Should().NotBeNull(
            $"{propertyName} is not consumed by the pipeline; it must be marked [Obsolete] so a future " +
            "re-wire is a deliberate, compiler-visible decision rather than a silent one");
        obsolete!.Message.Should().Contain("Retired",
            "the message must say the option is inert so a developer reading the warning is not misled");
        obsolete.Message.Should().Contain("Detection is never",
            "the message must name the replacement mechanism");
    }

    [Fact]
    public void The_replacement_surface_HealthEndpoints_is_live_and_seeded()
    {
        // The retired options' replacement must actually be wired: HealthEndpointOptions
        // is bound by the pipeline (HealthEndpointAtom reads the catalog; the ledger
        // trust logic reads ProbeUserAgents) and carries real defaults.
        var options = new BotDetectionOptions();

        options.HealthEndpoints.Should().NotBeNull();
        HealthEndpointOptions.DefaultPaths.Should().Contain("/healthz",
            "segment-boundary matching makes /healthz cover /healthz/freshness probes");
        HealthEndpointOptions.DefaultProbeUserAgents.Should().NotBeEmpty();
    }

    [Fact]
    public void Detection_middleware_does_not_take_or_read_the_retired_options()
    {
        // The atom-era middleware that replaced the contributor one is where a re-wire
        // would most plausibly land. Pin that it holds no BotDetectionOptions at all:
        // its constructor/method dependencies are the gates and the orchestrator.
        var middlewareType = typeof(Mostlylucid.BotDetection.Middleware.BotDetectionMiddleware);

        var ctorParams = middlewareType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        ctorParams.Should().NotContain(typeof(BotDetectionOptions),
            "the detection middleware must not hold options — detection is never gated on a path list");

        var optionFields = middlewareType
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(BotDetectionOptions) ||
                        f.FieldType == typeof(IOptions<BotDetectionOptions>))
            .ToList();

        optionFields.Should().BeEmpty("no field may smuggle the retired path options into the middleware");
    }
}
