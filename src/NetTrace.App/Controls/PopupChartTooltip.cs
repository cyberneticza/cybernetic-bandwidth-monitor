using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView.Painting;
using WpfChart = LiveChartsCore.SkiaSharpView.WPF.CartesianChart;

namespace NetTrace.App.Controls;

/// <summary>
/// Puts every chart's hover tooltip in a popup. LiveCharts draws its own tooltip inside the chart's
/// canvas, so a point near an edge, or a stacked chart with many series, cuts it off. A popup
/// floats over the window, sits beside the pointer, and flips to the other side near the window's
/// edges. Set <see cref="UsePopupProperty"/> on a chart; the implicit chart style sets it for all.
/// </summary>
public static class ChartTooltip
{
    public static readonly DependencyProperty UsePopupProperty = DependencyProperty.RegisterAttached(
        "UsePopup", typeof(bool), typeof(ChartTooltip), new PropertyMetadata(false, OnUsePopupChanged));

    public static bool GetUsePopup(DependencyObject d) => (bool)d.GetValue(UsePopupProperty);
    public static void SetUsePopup(DependencyObject d, bool value) => d.SetValue(UsePopupProperty, value);

    private static void OnUsePopupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WpfChart chart && (bool)e.NewValue) chart.Tooltip = new PopupChartTooltip(chart);
    }
}

/// <summary>A title (the hovered X, as the axis labels it) and one row per series: swatch, name, value.</summary>
public sealed class PopupChartTooltip : IChartTooltip
{
    private const double PointerGap = 16;

    private readonly WpfChart _chart;
    private readonly Popup _popup;
    private readonly TextBlock _title;
    private readonly StackPanel _rows;
    private readonly Border _panel;

    public PopupChartTooltip(WpfChart chart)
    {
        _chart = chart;

        _title = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        Themed(_title, TextBlock.ForegroundProperty, "TextBrush");
        Themed(_title, TextBlock.FontFamilyProperty, "UiFont");

        _rows = new StackPanel();

        _panel = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 9, 12, 10),
            MinWidth = 150,
            MaxWidth = 380,
            Margin = new Thickness(0, 0, 12, 12),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.25 },
            Child = new StackPanel { Children = { _title, _rows } }
        };
        Themed(_panel, Border.BackgroundProperty, "RaisedBrush");
        Themed(_panel, Border.BorderBrushProperty, "LineBrush");

        _popup = new Popup
        {
            Child = _panel,
            PlacementTarget = chart,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Focusable = false,
            StaysOpen = true
        };

        chart.MouseLeave += (_, _) => _popup.IsOpen = false;
        chart.Unloaded += (_, _) => _popup.IsOpen = false;
    }

    public void Show(IEnumerable<ChartPoint> foundPoints, Chart chart)
    {
        if (!_chart.Dispatcher.CheckAccess())
        {
            _chart.Dispatcher.BeginInvoke(() => Show(foundPoints.ToList(), chart));
            return;
        }

        var points = foundPoints.Where(p => p.Coordinate.PrimaryValue is not double.NaN).ToList();
        if (points.Count == 0 || !_chart.IsMouseOver)
        {
            _popup.IsOpen = false;
            return;
        }

        _title.Text = TitleFor(points[0]);
        _title.Visibility = string.IsNullOrWhiteSpace(_title.Text) ? Visibility.Collapsed : Visibility.Visible;

        // Zero rows only add noise to a stacked chart; keep them when every value is zero.
        var shown = points.Any(p => p.Coordinate.PrimaryValue != 0)
            ? points.Where(p => p.Coordinate.PrimaryValue != 0).ToList()
            : points;

        _rows.Children.Clear();
        foreach (var point in shown) _rows.Children.Add(Row(point));

        Place();
        _popup.IsOpen = true;
    }

    public void Hide(Chart chart)
    {
        if (_chart.Dispatcher.CheckAccess()) _popup.IsOpen = false;
        else _chart.Dispatcher.BeginInvoke(() => _popup.IsOpen = false);
    }

    /// <summary>Beside the pointer; on its left or above it when the window's edge is near.</summary>
    private void Place()
    {
        _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _panel.DesiredSize;
        var pointer = Mouse.GetPosition(_chart);

        double x = pointer.X + PointerGap;
        double y = pointer.Y + PointerGap;

        if (Window.GetWindow(_chart) is { } window)
        {
            // Keep clear of the window frame and its scrollbar, not just its outer edge.
            const double EdgeRoom = 28;
            var inWindow = Mouse.GetPosition(window);
            if (inWindow.X + PointerGap + size.Width > window.ActualWidth - EdgeRoom) x = pointer.X - PointerGap - size.Width + 12;
            if (inWindow.Y + PointerGap + size.Height > window.ActualHeight - EdgeRoom) y = pointer.Y - PointerGap - size.Height + 12;
        }

        _popup.HorizontalOffset = x;
        _popup.VerticalOffset = y;
    }

    private string TitleFor(ChartPoint point)
    {
        var axis = _chart.XAxes?.FirstOrDefault();
        if (axis?.Labeler is { } labeler)
        {
            try
            {
                return labeler(point.Coordinate.SecondaryValue);
            }
            catch (Exception)
            {
                // A labeller built for axis ticks may reject an in-between value; the rows still say enough.
            }
        }
        return string.Empty;
    }

    private static FrameworkElement Row(ChartPoint point)
    {
        var series = point.Context.Series;

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var swatch = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = SwatchFor(series)
        };

        var name = new TextBlock
        {
            Text = series.Name ?? string.Empty,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Themed(name, TextBlock.ForegroundProperty, "MutedBrush");
        Themed(name, TextBlock.FontFamilyProperty, "UiFont");

        var value = new TextBlock
        {
            Text = series.GetPrimaryToolTipText(point) ?? string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        Themed(value, TextBlock.ForegroundProperty, "TextBrush");
        Themed(value, TextBlock.FontFamilyProperty, "UiFont");

        Grid.SetColumn(name, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(swatch);
        grid.Children.Add(name);
        grid.Children.Add(value);
        return grid;
    }

    /// <summary>The series' fill, or its line or marker colour for series drawn without one.</summary>
    private static Brush SwatchFor(ISeries series)
    {
        foreach (var property in new[] { "Fill", "Stroke", "GeometryStroke" })
        {
            if (series.GetType().GetProperty(property)?.GetValue(series) is SolidColorPaint { } paint)
            {
                var c = paint.Color;
                // A faded fill (a line's area, a "previous" bar) reads better at full strength as a swatch.
                var brush = new SolidColorBrush(Color.FromArgb(Math.Max(c.Alpha, (byte)170), c.Red, c.Green, c.Blue));
                brush.Freeze();
                return brush;
            }
        }
        return Brushes.Gray;
    }

    private static void Themed(FrameworkElement element, DependencyProperty property, string key) =>
        element.SetResourceReference(property, key);
}
