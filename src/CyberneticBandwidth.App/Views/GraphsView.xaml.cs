using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiveChartsCore.Drawing;
using CyberneticBandwidth.App.ViewModels;

namespace CyberneticBandwidth.App.Views;

public partial class GraphsView : UserControl
{
    private const double PointerGap = 16;

    public GraphsView() => InitializeComponent();

    private GraphsViewModel? ViewModel => DataContext as GraphsViewModel;

    /// <summary>
    /// Turns the pointer into a slot of the selected day and keeps the hover panel beside it,
    /// switching to the pointer's left near the window's right edge so the panel never sits under
    /// the pointer (a popup under the pointer takes the mouse and makes the chart flicker).
    /// </summary>
    private void OnDetailMouseMove(object sender, MouseEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var position = e.GetPosition(DetailChart);
        var data = DetailChart.ScalePixelsToData(new LvcPointD(position.X, position.Y));
        vm.HoverAt(data.X);

        // The popup may extend past the chart; only the window's right edge makes it switch sides.
        double width = DetailHover.Child.RenderSize.Width;
        bool flip = Window.GetWindow(this) is { } window
                    && e.GetPosition(window).X + PointerGap + width > window.ActualWidth;

        DetailHover.HorizontalOffset = flip ? position.X - PointerGap - width : position.X + PointerGap;
        DetailHover.VerticalOffset = position.Y + PointerGap;
    }

    private void OnDetailMouseLeave(object sender, MouseEventArgs e) => ViewModel?.EndHover();
}
