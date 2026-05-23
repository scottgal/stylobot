using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI.Primitives;

public class PaginationNumberingTests
{
    [Fact]
    public void Compact_returns_all_pages_when_few()
    {
        var seq = PaginationNumbering.Compact(currentPage: 2, totalPages: 5, maxSlots: 7);
        Assert.Equal(new[] { (int?)1, 2, 3, 4, 5 }, seq);
    }

    [Fact]
    public void Compact_inserts_left_ellipsis_when_far_from_start()
    {
        // Current=10, total=14, slots=7 -> 1 ... 8 9 [10] 11 12 ... 14 is 9 slots, doesn't fit.
        // 7 slots = first + left-ellipsis + window-of-3-around-current + right-ellipsis + last.
        var seq = PaginationNumbering.Compact(currentPage: 10, totalPages: 14, maxSlots: 7);
        Assert.Equal(new[] { (int?)1, null, 9, 10, 11, null, 14 }, seq);
    }

    [Fact]
    public void Compact_inserts_right_ellipsis_only_when_near_start()
    {
        var seq = PaginationNumbering.Compact(currentPage: 2, totalPages: 14, maxSlots: 7);
        Assert.Equal(new[] { (int?)1, 2, 3, 4, 5, null, 14 }, seq);
    }

    [Fact]
    public void Compact_inserts_left_ellipsis_only_when_near_end()
    {
        var seq = PaginationNumbering.Compact(currentPage: 13, totalPages: 14, maxSlots: 7);
        Assert.Equal(new[] { (int?)1, null, 10, 11, 12, 13, 14 }, seq);
    }

    [Fact]
    public void Compact_handles_single_page()
    {
        var seq = PaginationNumbering.Compact(currentPage: 1, totalPages: 1, maxSlots: 7);
        Assert.Equal(new[] { (int?)1 }, seq);
    }
}
