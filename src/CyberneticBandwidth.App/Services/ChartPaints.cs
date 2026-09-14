using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace CyberneticBandwidth.App.Services;

/// <summary>
/// The paints every chart shares, built from the active theme. Paints are handed out new each
/// time rather than cached: LiveCharts tracks a paint per canvas, and one instance shared between
/// two charts is drawn by whichever registered it last.
/// </summary>
public static class ChartPaints
{
    public static SolidColorPaint Labels(ChartTheme theme) => new(theme.Muted);

    public static SolidColorPaint Separators(ChartTheme theme) => new(theme.Line) { StrokeThickness = 1 };

    public static SolidColorPaint Text(ChartTheme theme) => new(theme.Text);

    public static SolidColorPaint TooltipBackground(ChartTheme theme) => new(theme.Raised);

    public static SolidColorPaint Fill(SKColor colour, byte alpha = 255) => new(colour.WithAlpha(alpha));

    public static SolidColorPaint Stroke(SKColor colour, float thickness = 1.5f) =>
        new(colour) { StrokeThickness = thickness };
}
