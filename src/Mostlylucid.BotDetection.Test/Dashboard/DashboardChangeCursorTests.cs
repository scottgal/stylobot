using FluentAssertions;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     TDD coverage for <see cref="IDashboardChangeCursor"/> / <see cref="DashboardChangeCursor"/>.
///     All tests drive the cursor directly via <see cref="DashboardChangeCursor.AdvanceTick"/>
///     — no real scheduler wired up here.
/// </summary>
public sealed class DashboardChangeCursorTests
{
    private static DashboardChangeCursor CreateCursor() => new();

    // --- 1. Bump sets TickFor to CurrentTick ---

    [Fact]
    public void Bump_sets_TickFor_to_CurrentTick()
    {
        var cursor = CreateCursor();

        cursor.Bump("summary");

        cursor.TickFor("summary").Should().Be(cursor.CurrentTick);
    }

    // --- 2. AdvanceTick increments CurrentTick monotonically ---

    [Fact]
    public void AdvanceTick_increments_CurrentTick_monotonically()
    {
        var cursor = CreateCursor();
        var before = cursor.CurrentTick;

        cursor.AdvanceTick();
        var after1 = cursor.CurrentTick;
        cursor.AdvanceTick();
        var after2 = cursor.CurrentTick;

        after1.Should().BeGreaterThan(before);
        after2.Should().BeGreaterThan(after1);
    }

    // --- 3. Surface bumped before advance has TickFor less than new CurrentTick ---

    [Fact]
    public void Surface_bumped_before_advance_has_TickFor_less_than_new_CurrentTick()
    {
        var cursor = CreateCursor();
        cursor.Bump("countries");
        var tickAtBump = cursor.CurrentTick;

        cursor.AdvanceTick();

        cursor.TickFor("countries").Should().Be(tickAtBump);
        cursor.TickFor("countries").Should().BeLessThan(cursor.CurrentTick);
    }

    // --- 4. SurfacesChangedThisTick lists only surfaces bumped at current tick ---

    [Fact]
    public void SurfacesChangedThisTick_lists_only_surfaces_bumped_at_current_tick()
    {
        var cursor = CreateCursor();

        // Bump one surface, advance, then bump two more.
        cursor.Bump("old-surface");
        cursor.AdvanceTick();
        cursor.Bump("summary");
        cursor.Bump("top-bots");

        var changed = cursor.SurfacesChangedThisTick();

        changed.Should().BeEquivalentTo(new[] { "summary", "top-bots" });
        changed.Should().NotContain("old-surface");
    }

    // --- 5. Unrelated surface TickFor unaffected by bump on another surface ---

    [Fact]
    public void Unrelated_surface_TickFor_unaffected_by_bump()
    {
        var cursor = CreateCursor();

        cursor.Bump("summary");

        cursor.TickFor("countries").Should().Be(0);
    }
}
