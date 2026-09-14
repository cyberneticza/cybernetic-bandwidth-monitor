using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SkiaSharp;

namespace CyberneticBandwidth.App.Services;

public enum AppTheme
{
    Light,
    Dark
}

/// <summary>
/// The colours charts need. LiveCharts draws with Skia, outside WPF's resource system, so it
/// cannot follow a DynamicResource; view models rebuild their paints from this on every change.
/// Built from the active palette dictionary, so Dark.xaml and Light.xaml stay the only place a
/// colour is defined.
/// </summary>
public sealed record ChartTheme(
    SKColor Download,
    SKColor Upload,
    SKColor Alert,
    SKColor Text,
    SKColor Muted,
    SKColor Line,
    SKColor Surface,
    SKColor Raised,
    IReadOnlyList<SKColor> Series);

/// <summary>
/// Light or dark, following Windows' app theme unless the user has picked one. Swaps the palette
/// dictionary in slot 0 of the application resources and tells anything drawing its own colours.
/// </summary>
public sealed class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // Categorical colours for per-application series, in order of use. Each palette keeps the same
    // hue sequence; the light one is darker so thin lines and small areas hold up on white.
    private static readonly SKColor[] DarkSeries =
    [
        new(0x4F, 0xB6, 0xCE), new(0xE3, 0xA8, 0x57), new(0x7E, 0xA6, 0xE0), new(0x9C, 0xC4, 0x7E),
        new(0xD2, 0x70, 0x6B), new(0xB9, 0x8F, 0xD4), new(0x5F, 0xC9, 0xAE), new(0xD8, 0xC7, 0x6A)
    ];

    private static readonly SKColor[] LightSeries =
    [
        new(0x15, 0x80, 0x9A), new(0xC0, 0x7A, 0x1C), new(0x3F, 0x6F, 0xC2), new(0x5E, 0x93, 0x3F),
        new(0xB8, 0x43, 0x3D), new(0x87, 0x5B, 0xB0), new(0x1F, 0x9A, 0x7E), new(0xA8, 0x93, 0x22)
    ];

    private readonly PreferencesStore _preferences;
    private bool _listening;

    public ThemeService(PreferencesStore preferences) => _preferences = preferences;

    public ThemePreference Preference => _preferences.Current.Theme;

    public AppTheme Current { get; private set; } = AppTheme.Dark;

    public ChartTheme Chart { get; private set; } = null!;

    /// <summary>Raised on the UI thread after the palette has been swapped.</summary>
    public event Action? ThemeChanged;

    /// <summary>Call once on the UI thread before the first window is created.</summary>
    public void Initialize()
    {
        Apply(Resolve(), force: true);

        if (_listening) return;
        _listening = true;

        // Raised on a background thread when anything in personalisation changes, including the
        // app-mode switch in Settings > Colours.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (Preference == ThemePreference.System) Apply(Resolve(), force: false);
            });
        };
    }

    public void SetPreference(ThemePreference preference)
    {
        if (preference == Preference) return;

        _preferences.Current.Theme = preference;
        _preferences.Save();
        Apply(Resolve(), force: false);
    }

    private AppTheme Resolve() => Preference switch
    {
        ThemePreference.Light => AppTheme.Light,
        ThemePreference.Dark => AppTheme.Dark,
        _ => SystemUsesLightTheme() ? AppTheme.Light : AppTheme.Dark
    };

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        // Absent on older builds and fresh profiles, where Windows' default app mode is light.
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    private void Apply(AppTheme theme, bool force)
    {
        if (!force && theme == Current) return;

        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{theme}.xaml", UriKind.Absolute)
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = palette;
        else merged.Add(palette);

        Current = theme;
        Chart = BuildChartTheme(palette, theme);
        ThemeChanged?.Invoke();
    }

    private static ChartTheme BuildChartTheme(ResourceDictionary palette, AppTheme theme)
    {
        SKColor Get(string key)
        {
            var c = (Color)palette[key];
            return new SKColor(c.R, c.G, c.B, c.A);
        }

        return new ChartTheme(
            Download: Get("DownloadColor"),
            Upload: Get("UploadColor"),
            Alert: Get("AlertColor"),
            Text: Get("TextColor"),
            Muted: Get("MutedColor"),
            Line: Get("LineColor"),
            Surface: Get("SurfaceColor"),
            Raised: Get("RaisedColor"),
            Series: theme == AppTheme.Dark ? DarkSeries : LightSeries);
    }
}
