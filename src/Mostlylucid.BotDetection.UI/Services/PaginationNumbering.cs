namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Compact pagination numbering. Produces a sequence of page numbers (nullable int)
///     where <c>null</c> means "ellipsis". The total number of slots is clamped to
///     <paramref name="maxSlots"/> (default 7) so the rendered footer never wraps.
/// </summary>
public static class PaginationNumbering
{
    public static IReadOnlyList<int?> Compact(int currentPage, int totalPages, int maxSlots = 7)
    {
        if (totalPages <= 1) return new int?[] { 1 };

        if (totalPages <= maxSlots)
        {
            var all = new int?[totalPages];
            for (var i = 0; i < totalPages; i++) all[i] = i + 1;
            return all;
        }

        // Strategy: always show first page and last page.
        // In the middle we have (maxSlots - 2) interior slots.
        // An ellipsis costs 1 slot; when we need both ellipses the window shrinks.
        //
        // Phase 1: decide which ellipses are needed assuming a centred window.
        int interiorSlots = maxSlots - 2; // slots available between first and last
        // Minimum window around current when BOTH ellipses present: interiorSlots - 2
        int windowSize = interiorSlots - 2; // e.g. 7-2-2 = 3
        if (windowSize < 1) windowSize = 1;

        int halfW = windowSize / 2;
        int wStart = currentPage - halfW;
        int wEnd   = currentPage + halfW + (windowSize % 2 == 0 ? -1 : 0);

        // Clamp so window stays within [2, totalPages-1]
        if (wStart < 2)
        {
            wStart = 2;
            wEnd   = wStart + windowSize - 1;
        }
        if (wEnd > totalPages - 1)
        {
            wEnd   = totalPages - 1;
            wStart = wEnd - windowSize + 1;
            if (wStart < 2) wStart = 2;
        }

        bool leftEllipsis  = wStart > 2;
        bool rightEllipsis = wEnd < totalPages - 1;

        // When only one ellipsis is needed we have an extra slot — expand the window
        // toward the open side until the total fills maxSlots.
        if (!leftEllipsis && rightEllipsis)
        {
            // Can grow the window rightward or add pages between 1 and wStart.
            // Extend wEnd until we'd hit maxSlots: 1 + (wEnd - wStart + 1) + 1(null) + 1 = maxSlots
            // => wEnd - wStart + 1 = maxSlots - 3  => wEnd = wStart + maxSlots - 4
            wEnd = wStart + (maxSlots - 4);
            if (wEnd > totalPages - 1)
            {
                wEnd = totalPages - 1;
            }
            // Re-evaluate right ellipsis with expanded window.
            rightEllipsis = wEnd < totalPages - 1;
        }
        else if (leftEllipsis && !rightEllipsis)
        {
            // Extend wStart leftward: 1 + 1(null) + (wEnd - wStart + 1) + last = maxSlots
            // => wEnd - wStart + 1 = maxSlots - 3  => wStart = wEnd - (maxSlots - 4)
            wStart = wEnd - (maxSlots - 4);
            if (wStart < 2) wStart = 2;
            leftEllipsis = wStart > 2;
        }

        var result = new List<int?>(maxSlots);
        result.Add(1);
        if (leftEllipsis)
        {
            result.Add(null);
        }
        else
        {
            // Fill gap between 1 and wStart without ellipsis
            for (var p = 2; p < wStart; p++) result.Add(p);
        }

        for (var p = wStart; p <= wEnd; p++) result.Add(p);

        if (rightEllipsis)
        {
            result.Add(null);
        }
        else
        {
            // Fill gap between wEnd and totalPages without ellipsis
            for (var p = wEnd + 1; p < totalPages; p++) result.Add(p);
        }

        result.Add(totalPages);
        return result;
    }
}
