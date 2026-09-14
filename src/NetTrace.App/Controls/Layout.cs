using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetTrace.App.Controls;

/// <summary>
/// The app's responsive layout, in one flag. A page root sets <see cref="CompactBelowProperty"/>;
/// when the page is narrower than that, <see cref="IsCompactProperty"/> turns on for the root and
/// everything inside it (the property inherits), and every adaptive piece reacts to that one flag:
/// <see cref="AdaptiveColumns"/> stacks its columns, a <see cref="FillViewportProperty"/> grid stops
/// filling the window and lets its rows size to content, and styles can trigger on it directly.
///
/// One flag rather than a breakpoint per panel keeps a page from being half stacked.
/// </summary>
public static class Layout
{
    public static readonly DependencyProperty CompactBelowProperty = DependencyProperty.RegisterAttached(
        "CompactBelow", typeof(double), typeof(Layout), new PropertyMetadata(double.NaN, OnCompactBelowChanged));

    public static double GetCompactBelow(DependencyObject d) => (double)d.GetValue(CompactBelowProperty);
    public static void SetCompactBelow(DependencyObject d, double value) => d.SetValue(CompactBelowProperty, value);

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.RegisterAttached(
        "IsCompact", typeof(bool), typeof(Layout),
        new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure,
            OnIsCompactChanged));

    public static bool GetIsCompact(DependencyObject d) => (bool)d.GetValue(IsCompactProperty);
    public static void SetIsCompact(DependencyObject d, bool value) => d.SetValue(IsCompactProperty, value);

    /// <summary>
    /// On a Grid inside a vertical ScrollViewer: while the page is wide, the grid is exactly as tall
    /// as the viewport (never shorter than its MinHeight), so star rows share the window as they
    /// would outside a ScrollViewer and a short window scrolls instead of squashing. While compact,
    /// star rows become Auto and the page simply scrolls; give star-row content a MinHeight.
    /// </summary>
    public static readonly DependencyProperty FillViewportProperty = DependencyProperty.RegisterAttached(
        "FillViewport", typeof(bool), typeof(Layout), new PropertyMetadata(false, OnFillViewportChanged));

    public static bool GetFillViewport(DependencyObject d) => (bool)d.GetValue(FillViewportProperty);
    public static void SetFillViewport(DependencyObject d, bool value) => d.SetValue(FillViewportProperty, value);

    /// <summary>
    /// On a ScrollViewer, or a control with one inside (DataGrid): the mouse wheel scrolls it while it
    /// can move that way, then scrolls the page around it. Without this a table or a sideways-only
    /// strip swallows every wheel turn and the page stops scrolling under the pointer.
    /// </summary>
    public static readonly DependencyProperty PassWheelAtEndProperty = DependencyProperty.RegisterAttached(
        "PassWheelAtEnd", typeof(bool), typeof(Layout), new PropertyMetadata(false, OnPassWheelAtEndChanged));

    public static bool GetPassWheelAtEnd(DependencyObject d) => (bool)d.GetValue(PassWheelAtEndProperty);
    public static void SetPassWheelAtEnd(DependencyObject d, bool value) => d.SetValue(PassWheelAtEndProperty, value);

    private static void OnPassWheelAtEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d) return;

        var scroller = d as ScrollViewer ?? FindChildScrollViewer(d);
        if (scroller is null) return;

        bool canMove = e.Delta > 0
            ? scroller.VerticalOffset > 0
            : scroller.VerticalOffset < scroller.ScrollableHeight;
        if (canMove) return;

        if (VisualTreeHelper.GetParent(d) is UIElement parent)
        {
            e.Handled = true;
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            });
        }
    }

    private static ScrollViewer? FindChildScrollViewer(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is ScrollViewer scroller) return scroller;
            if (FindChildScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private static readonly ConditionalWeakTable<RowDefinition, object> OriginalHeights = new();

    private static void OnCompactBelowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.SizeChanged -= UpdateCompact;
        element.SizeChanged += UpdateCompact;
        Apply(element);
    }

    private static void UpdateCompact(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) Apply((FrameworkElement)sender);
    }

    private static void Apply(FrameworkElement element)
    {
        double below = GetCompactBelow(element);
        if (double.IsNaN(below) || element.ActualWidth <= 0) return;

        // Hysteresis: a scrollbar appearing when the page stacks narrows it by a few pixels, which
        // must not flip it straight back.
        bool compact = GetIsCompact(element);
        bool next = compact ? element.ActualWidth < below + 24 : element.ActualWidth < below;
        if (next != compact) SetIsCompact(element, next);
    }

    private static void OnIsCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Grid grid && GetFillViewport(grid)) ApplyFill(grid);
    }

    private static void OnFillViewportChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid) return;

        // A finite height from the very first layout pass. Loaded only comes after that pass, and
        // until then a ScrollViewer offers its content unlimited height: a DataGrid measured that
        // way turns off virtualisation and builds every row, which for the Connections log's 5,000
        // rows froze the app for over half a minute each time the page opened.
        grid.Height = grid.MinHeight > 0 ? grid.MinHeight : 600;
        grid.Initialized += (_, _) =>
        {
            if (double.IsNaN(grid.Height) || grid.Height < grid.MinHeight) grid.Height = Math.Max(grid.MinHeight, 600);
        };

        grid.Loaded += (_, _) =>
        {
            if (FindScrollViewer(grid) is { } scroller)
            {
                scroller.SizeChanged -= OnViewportChanged;
                scroller.SizeChanged += OnViewportChanged;
            }
            ApplyFill(grid);
        };
    }

    private static void OnViewportChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer { Content: Grid grid } && GetFillViewport(grid)) ApplyFill(grid);
    }

    private static void ApplyFill(Grid grid)
    {
        bool compact = GetIsCompact(grid);

        foreach (var row in grid.RowDefinitions)
        {
            if (!OriginalHeights.TryGetValue(row, out var original))
            {
                original = row.Height;
                OriginalHeights.Add(row, original);
            }

            var height = (GridLength)original;
            row.Height = compact && height.IsStar ? GridLength.Auto : height;
        }

        if (compact || FindScrollViewer(grid) is not { } scroller)
        {
            grid.Height = double.NaN;
            return;
        }

        double available = scroller.ViewportHeight > 0 ? scroller.ViewportHeight : scroller.ActualHeight;
        grid.Height = Math.Max(grid.MinHeight, available - grid.Margin.Top - grid.Margin.Bottom);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        for (var parent = VisualTreeHelper.GetParent(d); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scroller) return scroller;
        }
        return null;
    }
}

/// <summary>
/// Columns side by side while the page is wide, stacked full-width while it is compact.
/// Side by side, each child gets <see cref="FixedWidthProperty"/> or a share of the rest by
/// <see cref="WeightProperty"/>, and all share the panel's height. Stacked, each child is
/// <see cref="StackedHeightProperty"/> tall, or as tall as its content when that is not set.
/// </summary>
public sealed class AdaptiveColumns : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetWeight(DependencyObject d) => (double)d.GetValue(WeightProperty);
    public static void SetWeight(DependencyObject d, double value) => d.SetValue(WeightProperty, value);

    public static readonly DependencyProperty FixedWidthProperty = DependencyProperty.RegisterAttached(
        "FixedWidth", typeof(double), typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetFixedWidth(DependencyObject d) => (double)d.GetValue(FixedWidthProperty);
    public static void SetFixedWidth(DependencyObject d, double value) => d.SetValue(FixedWidthProperty, value);

    public static readonly DependencyProperty StackedHeightProperty = DependencyProperty.RegisterAttached(
        "StackedHeight", typeof(double), typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetStackedHeight(DependencyObject d) => (double)d.GetValue(StackedHeightProperty);
    public static void SetStackedHeight(DependencyObject d, double value) => d.SetValue(StackedHeightProperty, value);

    private bool IsStacked => Layout.GetIsCompact(this);

    private IEnumerable<UIElement> Visible => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed);

    protected override Size MeasureOverride(Size available)
    {
        var children = Visible.ToList();
        if (children.Count == 0) return default;

        if (IsStacked)
        {
            double y = 0, width = 0;
            foreach (var child in children)
            {
                double fixedHeight = GetStackedHeight(child);
                child.Measure(new Size(available.Width, double.IsNaN(fixedHeight) ? double.PositiveInfinity : fixedHeight));
                y += (double.IsNaN(fixedHeight) ? child.DesiredSize.Height : fixedHeight) + Spacing;
                width = Math.Max(width, child.DesiredSize.Width);
            }
            return new Size(double.IsInfinity(available.Width) ? width : available.Width, y - Spacing);
        }

        var widths = ColumnWidths(children, available.Width);
        double height = 0, total = 0;
        for (int i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(widths[i], available.Height));
            height = Math.Max(height, children[i].DesiredSize.Height);
            total += widths[i];
        }
        return new Size(total + Spacing * (children.Count - 1), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Visible.ToList();

        if (IsStacked)
        {
            double y = 0;
            foreach (var child in children)
            {
                double fixedHeight = GetStackedHeight(child);
                double h = double.IsNaN(fixedHeight) ? child.DesiredSize.Height : fixedHeight;
                child.Arrange(new Rect(0, y, final.Width, h));
                y += h + Spacing;
            }
            return final;
        }

        var widths = ColumnWidths(children, final.Width);
        double x = 0;
        for (int i = 0; i < children.Count; i++)
        {
            children[i].Arrange(new Rect(x, 0, widths[i], final.Height));
            x += widths[i] + Spacing;
        }
        return final;
    }

    private double[] ColumnWidths(IReadOnlyList<UIElement> children, double available)
    {
        var widths = new double[children.Count];
        if (double.IsInfinity(available))
        {
            for (int i = 0; i < children.Count; i++)
            {
                double fixedWidth = GetFixedWidth(children[i]);
                widths[i] = double.IsNaN(fixedWidth) ? 400 : fixedWidth;
            }
            return widths;
        }

        double remaining = available - Spacing * (children.Count - 1);
        double weights = 0;
        for (int i = 0; i < children.Count; i++)
        {
            double fixedWidth = GetFixedWidth(children[i]);
            if (double.IsNaN(fixedWidth)) weights += GetWeight(children[i]);
            else remaining -= widths[i] = fixedWidth;
        }

        remaining = Math.Max(0, remaining);
        for (int i = 0; i < children.Count; i++)
        {
            if (double.IsNaN(GetFixedWidth(children[i])))
                widths[i] = weights <= 0 ? 0 : remaining * GetWeight(children[i]) / weights;
        }
        return widths;
    }
}

/// <summary>
/// Equal-width tiles that wrap: as many columns as fit at <see cref="MinItemWidth"/> (up to
/// <see cref="MaxColumns"/>), stretched to fill the row. Rows are as tall as their tallest tile.
/// For summary cards and metric tiles, which should never overlap or clip on a narrow window.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(180d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(int.MaxValue, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Space between rows; the column spacing when not set.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private double VerticalGap => double.IsNaN(RowSpacing) ? Spacing : RowSpacing;

    private List<UIElement> Visible => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private int Columns(double width, int count)
    {
        if (count == 0) return 1;
        int fit = double.IsInfinity(width) ? count : (int)Math.Floor((width + Spacing) / (MinItemWidth + Spacing));
        fit = Math.Clamp(fit, 1, Math.Max(1, Math.Min(MaxColumns, count)));

        // Balanced rows: four tiles that fit three across go two by two rather than three and one.
        int rows = (int)Math.Ceiling(count / (double)fit);
        return (int)Math.Ceiling(count / (double)rows);
    }

    protected override Size MeasureOverride(Size available)
    {
        var children = Visible;
        int columns = Columns(available.Width, children.Count);
        double itemWidth = double.IsInfinity(available.Width)
            ? MinItemWidth
            : Math.Max(0, (available.Width - Spacing * (columns - 1)) / columns);

        double height = 0, rowHeight = 0;
        for (int i = 0; i < children.Count; i++)
        {
            children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
            if (i % columns == columns - 1 || i == children.Count - 1)
            {
                height += rowHeight + VerticalGap;
                rowHeight = 0;
            }
        }

        double width = double.IsInfinity(available.Width) ? itemWidth * columns + Spacing * (columns - 1) : available.Width;
        return new Size(width, Math.Max(0, height - VerticalGap));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Visible;
        int columns = Columns(final.Width, children.Count);
        double itemWidth = Math.Max(0, (final.Width - Spacing * (columns - 1)) / columns);

        double y = 0;
        for (int start = 0; start < children.Count; start += columns)
        {
            var row = children.Skip(start).Take(columns).ToList();
            double rowHeight = row.Max(c => c.DesiredSize.Height);
            for (int i = 0; i < row.Count; i++)
                row[i].Arrange(new Rect(i * (itemWidth + Spacing), y, itemWidth, rowHeight));
            y += rowHeight + VerticalGap;
        }
        return final;
    }
}
