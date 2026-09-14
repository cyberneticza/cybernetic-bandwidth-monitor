using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Services;

namespace CyberneticBandwidth.App.ViewModels;

public sealed partial class NavigationItem(string label, string glyph, string description, ObservableObject content) : ObservableObject
{
    public string Label { get; } = label;

    /// <summary>One line under the page title: what the page is for.</summary>
    public string Description { get; } = description;

    /// <summary>Segoe Fluent Icons code point.</summary>
    public string Glyph { get; } = glyph;

    public ObservableObject Content { get; } = content;
}

public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty] private NavigationItem _selectedItem;

    // The rail shows two lists, pages on top and Settings and About at the bottom. Each list
    // binds its own selection; exactly one of them is set at a time.
    [ObservableProperty] private NavigationItem? _pageSelection;
    [ObservableProperty] private NavigationItem? _footerSelection;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Icons only, for narrow windows. Set by the window from its width.</summary>
    [ObservableProperty] private bool _isRailCompact;

    public ObservableCollection<NavigationItem> Items { get; }

    public ObservableCollection<NavigationItem> FooterItems { get; }

    public string VersionLabel { get; }

    public ShellViewModel(
        DashboardViewModel dashboard,
        GraphsViewModel graphs,
        HistoryViewModel history,
        NetworkViewModel network,
        SpeedTestViewModel speedTest,
        LogsViewModel logs,
        SettingsViewModel settings,
        AboutViewModel about,
        LiveActivityBroker broker,
        NavigationRequests navigation)
    {
        Items =
        [
            new NavigationItem("Live", "\uE9D9", "What is using your connection right now", dashboard),
            new NavigationItem("Trends", "\uE9D2", "Daily usage over a period, and any day in detail", graphs),
            new NavigationItem("History", "\uE81C", "Reports, patterns and busy times for any range of days", history),
            new NavigationItem("Network", "\uE968", "What you are connected to, and how well it is working", network),
            new NavigationItem("Speed test", "\uEC4A", "How fast your connection is, nearby and around the world", speedTest),
            new NavigationItem("Connections", "\uE9D5", "Every connection programs on this PC open and close", logs)
        ];

        FooterItems =
        [
            new NavigationItem("Settings", "\uE713", "Appearance, alerts, budgets and storage", settings),
            new NavigationItem("About", "\uE946", "What Cybernetic Bandwidth Monitor is and how it works", about)
        ];

        _selectedItem = Items[0];
        _pageSelection = Items[0];
        _statusMessage = broker.StatusMessage;
        broker.StatusChanged += message => StatusMessage = message;

        VersionLabel = $"Version {about.Version}";

        navigation.Requested += type =>
        {
            var target = Items.Concat(FooterItems).FirstOrDefault(i => i.Content.GetType() == type);
            if (target is not null) SelectedItem = target;
        };
    }

    // History pages read from the store, which grows while the app runs (and Trends names are
    // refreshed at startup); the view models are singletons that would otherwise keep showing
    // whatever they loaded when the window was built.
    partial void OnPageSelectionChanged(NavigationItem? value)
    {
        if (value is not null) SelectedItem = value;
    }

    partial void OnFooterSelectionChanged(NavigationItem? value)
    {
        if (value is not null) SelectedItem = value;
    }

    partial void OnSelectedItemChanged(NavigationItem? oldValue, NavigationItem newValue)
    {
        // Whichever list the page lives in shows it selected; the other shows nothing.
        bool inFooter = FooterItems.Contains(newValue);
        PageSelection = inFooter ? null : newValue;
        FooterSelection = inFooter ? newValue : null;

        if (oldValue?.Content is NetworkViewModel left) left.Hide();

        switch (newValue.Content)
        {
            case GraphsViewModel graphs: _ = graphs.ReloadAsync(); break;
            case HistoryViewModel history: _ = history.ReloadAsync(); break;
            case NetworkViewModel network: _ = network.ReloadAsync(); break;
            case SpeedTestViewModel speedTest: _ = speedTest.ReloadAsync(); break;
        }
    }
}
