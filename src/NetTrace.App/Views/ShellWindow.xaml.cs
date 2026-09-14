using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetTrace.App.Services;
using NetTrace.App.ViewModels;

namespace NetTrace.App.Views;

public partial class ShellWindow : Window
{
    /// <summary>Below this window width the rail shows icons only, leaving the room to the page.</summary>
    private const double CompactRailBelow = 1100;

    private static readonly Duration PageFade = TimeSpan.FromMilliseconds(220);

    private readonly ThemeService _theme;

    public ShellWindow(ShellViewModel viewModel, ThemeService theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        _theme = theme;

        WindowFrame.FitToWorkArea(this);
        SourceInitialized += (_, _) => ApplyFrameTheme();
        theme.ThemeChanged += () => ApplyFrameTheme();

        SizeChanged += (_, _) => viewModel.IsRailCompact = ActualWidth < CompactRailBelow;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedItem)) PlayPageTransition();
    }

    /// <summary>
    /// The new page and its title rise a few pixels into place while fading in. Short enough never
    /// to be waited on; it just makes a page change read as a change.
    /// </summary>
    private void PlayPageTransition()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        foreach (var element in new UIElement[] { PageHost, PageHeader })
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, PageFade) { EasingFunction = ease });

        PageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, PageFade) { EasingFunction = ease });
    }

    private void ApplyFrameTheme() => WindowFrame.ApplyTheme(this,
        dark: _theme.Current == AppTheme.Dark,
        caption: (Color)FindResource("SurfaceColor"),
        text: (Color)FindResource("TextColor"),
        border: (Color)FindResource("LineColor"));
}
