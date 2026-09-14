using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;

namespace CyberneticBandwidth.App.Services;

/// <summary>
/// What every chart's axes share: label size, and round steps for byte scales. Left to itself an
/// axis divides the data's range evenly in raw bytes, which in binary units reads as 953.7 MB,
/// 1.9 GB, 2.8 GB; stepping by 1, 2 or 5 of a unit reads as 1 GB, 2 GB, 3 GB.
/// </summary>
public static class ChartScale
{
    /// <summary>Axis labels at the size of the pages' small text, not LiveCharts' larger default.</summary>
    public const float LabelSize = 12;

    private static readonly double[] Multiples = [1, 2, 5, 10, 20, 50, 100, 200, 500];

    /// <summary>
    /// Fixes a byte axis to start at zero and rise in about <paramref name="steps"/> round steps to
    /// just above <paramref name="max"/>. Only for axes that are not zoomed, since it sets the limits.
    /// </summary>
    public static void Bytes(Axis axis, double max, int steps = 4)
    {
        double step = RoundBytes(Math.Max(max, 1) / steps);
        axis.MinLimit = 0;
        axis.MaxLimit = Math.Max(step, Math.Ceiling(max / step) * step);
        axis.MinStep = step;
        axis.ForceStepToMin = true;
    }

    /// <summary>
    /// The tallest point the series draw: stacked series add up at each X, others count on their own.
    /// </summary>
    public static double MaxOf(IEnumerable<ISeries> series)
    {
        double max = 0;
        var stacks = new Dictionary<double, double>();

        foreach (var s in series)
        {
            if (s.Values is not System.Collections.IEnumerable values) continue;
            bool stacked = s.GetType().Name.StartsWith("Stacked", StringComparison.Ordinal);

            foreach (var point in values.OfType<ObservablePoint>())
            {
                if (point.Y is not { } y || point.X is not { } x) continue;
                if (stacked)
                {
                    stacks[x] = stacks.GetValueOrDefault(x) + y;
                    max = Math.Max(max, stacks[x]);
                }
                else
                {
                    max = Math.Max(max, y);
                }
            }
        }
        return max;
    }

    /// <summary>The smallest 1, 2 or 5 × a power of ten, of a binary unit (B, KB, MB, …), at least <paramref name="bytes"/>.</summary>
    public static double RoundBytes(double bytes)
    {
        double unit = 1;
        while (bytes >= unit * ByteFormat.Kilobyte) unit *= ByteFormat.Kilobyte;

        double inUnit = bytes / unit;
        foreach (double multiple in Multiples)
        {
            if (multiple >= inUnit) return multiple * unit;
        }
        return unit * ByteFormat.Kilobyte;
    }
}
