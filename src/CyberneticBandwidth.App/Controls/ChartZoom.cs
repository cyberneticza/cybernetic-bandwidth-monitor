using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CyberneticBandwidth.App.Controls;

/// <summary>
/// Zoomable charts sit on scrolling pages, where a plain wheel turn is meant for the page. With
/// <see cref="RequireCtrlProperty"/> on, the wheel zooms the chart only while Ctrl is held; otherwise
/// it scrolls the page and a short hint over the chart says how to zoom, the way embedded maps do.
/// Dragging to pan and double-clicking to reset are unaffected.
/// </summary>
public static class ChartZoom
{
    public static readonly DependencyProperty RequireCtrlProperty = DependencyProperty.RegisterAttached(
        "RequireCtrl", typeof(bool), typeof(ChartZoom), new PropertyMetadata(false, OnRequireCtrlChanged));

    public static bool GetRequireCtrl(DependencyObject d) => (bool)d.GetValue(RequireCtrlProperty);
    public static void SetRequireCtrl(DependencyObject d, bool value) => d.SetValue(RequireCtrlProperty, value);

    private static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(ZoomHintAdorner), typeof(ChartZoom));

    private static bool CtrlHeld => (Keyboard.Modifiers & ModifierKeys.Control) != 0;

    private static void OnRequireCtrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        element.RemoveHandler(UIElement.MouseWheelEvent, (MouseWheelEventHandler)AfterChartWheel);
        if (!(bool)e.NewValue) return;

        element.PreviewMouseWheel += OnPreviewMouseWheel;
        // After the chart has zoomed, so the page around it does not scroll as well.
        element.AddHandler(UIElement.MouseWheelEvent, (MouseWheelEventHandler)AfterChartWheel, handledEventsToo: true);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CtrlHeld || sender is not FrameworkElement chart) return;

        // Handling the tunnelling event keeps the chart from seeing the wheel at all; the page gets it instead.
        e.Handled = true;
        if (VisualTreeHelper.GetParent(chart) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            });
        }

        ShowHint(chart);
    }

    private static void AfterChartWheel(object sender, MouseWheelEventArgs e)
    {
        if (CtrlHeld) e.Handled = true;
    }

    private static void ShowHint(FrameworkElement chart)
    {
        if (chart.GetValue(HintProperty) is not ZoomHintAdorner hint)
        {
            if (AdornerLayer.GetAdornerLayer(chart) is not { } layer) return;
            hint = new ZoomHintAdorner(chart);
            layer.Add(hint);
            chart.SetValue(HintProperty, hint);
            chart.Unloaded += (_, _) =>
            {
                AdornerLayer.GetAdornerLayer(chart)?.Remove(hint);
                chart.ClearValue(HintProperty);
            };
        }
        hint.Flash();
    }

    /// <summary>A pill in the middle of the chart that fades in, holds while the wheel keeps turning, and fades out.</summary>
    private sealed class ZoomHintAdorner : Adorner
    {
        private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(1300);

        private readonly Border _pill;
        private readonly DispatcherTimer _timer;
        private bool _shown;

        public ZoomHintAdorner(UIElement chart) : base(chart)
        {
            IsHitTestVisible = false;

            var text = new TextBlock
            {
                Text = "Hold Ctrl and scroll to zoom",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var glyph = new TextBlock
            {
                Text = "\uE71E",
                FontSize = 14,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "DownloadBrush");

            _pill = new Border
            {
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(16, 8, 18, 8),
                BorderThickness = new Thickness(1),
                Opacity = 0,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, text } },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.25 }
            };
            _pill.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
            _pill.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            AddVisualChild(_pill);

            _timer = new DispatcherTimer { Interval = Hold };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                Fade(0);
                _shown = false;
            };
        }

        public void Flash()
        {
            if (!_shown)
            {
                Fade(1);
                _shown = true;
            }
            _timer.Stop();
            _timer.Start();
        }

        private void Fade(double to) =>
            _pill.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 140 : 260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _pill;

        protected override Size MeasureOverride(Size constraint)
        {
            _pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = _pill.DesiredSize;
            _pill.Arrange(new Rect(
                Math.Max(0, (finalSize.Width - size.Width) / 2),
                Math.Max(0, (finalSize.Height - size.Height) / 2),
                size.Width, size.Height));
            return finalSize;
        }
    }
}
