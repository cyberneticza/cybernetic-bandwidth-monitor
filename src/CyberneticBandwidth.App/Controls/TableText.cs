using System.Windows;
using System.Windows.Controls;

namespace CyberneticBandwidth.App.Controls;

/// <summary>
/// Text columns that end in an ellipsis when they are narrower than their text, instead of cutting a
/// word off mid-letter. An implicit TextBlock style cannot do this: DataGridTextColumn gives its cells
/// a default ElementStyle of its own, which wins. So on load, every text column without an
/// ElementStyle gets <see cref="TrimmedCell"/>; columns that set their own (numbers) keep theirs.
/// Set on every DataGrid by the implicit style in Controls.xaml.
/// </summary>
public static class TableText
{
    public static readonly DependencyProperty TrimProperty = DependencyProperty.RegisterAttached(
        "Trim", typeof(bool), typeof(TableText), new PropertyMetadata(false, OnTrimChanged));

    public static bool GetTrim(DependencyObject d) => (bool)d.GetValue(TrimProperty);
    public static void SetTrim(DependencyObject d, bool value) => d.SetValue(TrimProperty, value);

    private static readonly Style TrimmedCell = CreateTrimmedCell();

    private static Style CreateTrimmedCell()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Seal();
        return style;
    }

    private static void OnTrimChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid || !(bool)e.NewValue) return;
        grid.Loaded -= Apply;
        grid.Loaded += Apply;
    }

    private static void Apply(object sender, RoutedEventArgs e)
    {
        foreach (var column in ((DataGrid)sender).Columns.OfType<DataGridTextColumn>())
        {
            if (column.ElementStyle is null || column.ElementStyle == DataGridTextColumn.DefaultElementStyle)
                column.ElementStyle = TrimmedCell;
        }
    }
}
