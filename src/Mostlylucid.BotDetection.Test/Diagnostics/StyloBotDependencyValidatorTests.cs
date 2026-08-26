using FluentAssertions;
using Mostlylucid.BotDetection.UI.Diagnostics;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Diagnostics;

/// <summary>
///     Fail-fast dependency-check coverage (issue #124). The dashboard's
///     REQUIRED pack assemblies must be present at composition time; the
///     validator throws with an actionable message when one is missing.
///     Prometheus is deliberately NOT required -- it is an optional add-on
///     whose absence degrades gracefully.
/// </summary>
public sealed class StyloBotDependencyValidatorTests
{
    [Fact]
    public void ValidateRequiredPacks_passes_when_all_required_assemblies_present()
    {
        var act = () => StyloBotDependencyValidator.ValidateRequiredPacks(_ => true);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateRequiredPacks_throws_with_actionable_message_when_a_pack_is_missing()
    {
        // Simulate a host whose restore dropped the OpenApi assembly.
        var act = () => StyloBotDependencyValidator.ValidateRequiredPacks(
            name => name != "Mostlylucid.BotDetection.OpenApi");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Mostlylucid.BotDetection.OpenApi*")
            .WithMessage("*dotnet add package*",
                "the message MUST name the exact install command so a consumer can self-heal.");
    }

    [Fact]
    public void ValidateRequiredPacks_reports_the_first_missing_required_pack()
    {
        var first = StyloBotDependencyValidator.RequiredPacks[0];

        var act = () => StyloBotDependencyValidator.ValidateRequiredPacks(_ => false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{first.PackageId}*");
    }

    [Fact]
    public void ValidateRequiredPacks_does_not_require_the_optional_prometheus_pack()
    {
        // Simulate a host WITHOUT the Prometheus pack (the optional add-on).
        // The validator must still pass -- the dashboard degrades gracefully
        // (no meter-health tile) instead of failing boot.
        var act = () => StyloBotDependencyValidator.ValidateRequiredPacks(
            name => !name.Contains("PrometheusPack"));

        act.Should().NotThrow(
            "Prometheus is an optional add-on, not part of the dashboard's REQUIRED pack contract.");
    }

    [Fact]
    public void ValidateRequiredPacks_passes_with_the_real_assembly_loader()
    {
        // No injected seam: exercise the actual Assembly.Load + exception-catch path.
        // The test host references the UI assembly, which references core + OpenApi, so
        // all REQUIRED packs are present and the default loader must succeed.
        var act = () => StyloBotDependencyValidator.ValidateRequiredPacks();
        act.Should().NotThrow(
            "the real Assembly.Load path must resolve every required pack in a host that references the dashboard.");
    }
}
