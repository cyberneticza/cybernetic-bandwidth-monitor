using System.Windows.Controls;

namespace NetTrace.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    /// <summary>
    /// A range longer than the panel is wide scrolls the calendar; start it at the most recent weeks,
    /// which are what a range ending today is mostly about.
    /// </summary>
    private void OnCalendarScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentWidthChange != 0 && sender is ScrollViewer scroller) scroller.ScrollToRightEnd();
    }
}
