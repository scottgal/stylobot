using System;
using System.Globalization;
using System.Text;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds an SVG path string for a sparkline. Server-side -- the rendered partial
///     emits the path directly so the browser does no work to draw the trend.
/// </summary>
public static class SparklinePathBuilder
{
    /// <summary>
    ///     Build an SVG <c>d</c> attribute for an int[] series.
    ///     Returns "" for an empty array.
    ///     Y-axis is auto-scaled to the max value unless <paramref name="max"/> is supplied.
    /// </summary>
    public static string Build(int[] values, int width, int height, int? max = null)
    {
        if (values.Length == 0) return "";
        if (values.Length == 1) return "M0,0 L0,0";

        int peak = max ?? MaxOrOne(values);
        if (peak <= 0) peak = 1;

        double xStep = (double)width / (values.Length - 1);
        var sb = new StringBuilder(values.Length * 12);

        for (int i = 0; i < values.Length; i++)
        {
            double x = i * xStep;
            double y = height - (double)values[i] / peak * height;
            sb.Append(i == 0 ? "M" : " L");
            sb.Append(((int)Math.Round(x)).ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(((int)Math.Round(y)).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static int MaxOrOne(int[] values)
    {
        int m = 0;
        for (int i = 0; i < values.Length; i++) if (values[i] > m) m = values[i];
        return m == 0 ? 1 : m;
    }
}
