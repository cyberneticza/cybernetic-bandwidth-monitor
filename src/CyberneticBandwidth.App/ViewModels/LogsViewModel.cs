using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Models;
using CyberneticBandwidth.Core.Services;

namespace CyberneticBandwidth.App.ViewModels;

public sealed class ConnectionEventRow(ConnectionEventRecord record)
{
    public DateTime ObservedLocal { get; } = record.ObservedUtc.LocalDateTime;
    public string Kind { get; } = record.Kind == ConnectionEventKind.Opened ? "Opened" : "Closed";
    public bool IsOpen { get; } = record.Kind == ConnectionEventKind.Opened;
    public int Pid { get; } = record.Pid;
    public string ProcessName { get; } = record.ProcessName;
    public string ProtocolName { get; } = Protocols.Name(record.Protocol);
    public string LocalEndpoint { get; } = $"{record.LocalAddress}:{record.LocalPort}";

    public string RemoteEndpoint { get; } = record.RemoteAddress is null
        ? "—"
        : $"{record.RemoteAddress}:{record.RemotePort}";

    public string State { get; } = record.State == TcpState.Unknown ? "—" : record.State.ToString();

    /// <summary>What the port usually means: the remote one when there is a peer, else the local one.</summary>
    public string Service { get; } = WellKnownPorts.Describe(record.RemotePort ?? record.LocalPort);

    public string SearchText { get; } =
        $"{record.ProcessName} {record.Pid} {Protocols.Name(record.Protocol)} " +
        $"{record.LocalAddress}:{record.LocalPort} {record.RemoteAddress}:{record.RemotePort} " +
        WellKnownPorts.Describe(record.RemotePort ?? record.LocalPort);
}

public sealed partial class LogsViewModel : ObservableObject
{
    private const int MaxRows = 5_000;

    private readonly IServiceScopeFactory _scopes;
    private readonly PreferencesStore _preferences;
    private readonly ILogger<LogsViewModel> _log;
    private readonly ObservableCollection<ConnectionEventRow> _rows = [];

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _protocolFilter = "All";
    [ObservableProperty] private bool _isLiveTailing = true;
    [ObservableProperty] private bool _showHelp;

    public ICollectionView Events { get; }

    public IReadOnlyList<string> ProtocolOptions { get; } = ["All", "TCP", "UDP"];

    public LogsViewModel(IServiceScopeFactory scopes, LiveActivityBroker broker, PreferencesStore preferences, ILogger<LogsViewModel> log)
    {
        _scopes = scopes;
        _preferences = preferences;
        _log = log;
        _showHelp = preferences.Current.ShowConnectionsHelp;

        Events = CollectionViewSource.GetDefaultView(_rows);
        Events.Filter = Matches;

        broker.ConnectionEventsRecorded += OnEvents;
        _ = LoadHistoryAsync();
    }

    // Filtering in memory over a bounded buffer keeps typing responsive. Re-querying SQLite on
    // every keystroke would be both slower and pointless at this row count.
    partial void OnFilterTextChanged(string value) => Events.Refresh();
    partial void OnProtocolFilterChanged(string value) => Events.Refresh();

    private bool Matches(object item)
    {
        if (item is not ConnectionEventRow row) return false;

        if (!ProtocolFilter.Equals("All", StringComparison.Ordinal) &&
            !row.ProtocolName.Equals(ProtocolFilter, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(FilterText)) return true;

        return row.SearchText.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void OnEvents(IReadOnlyList<ConnectionEventRecord> events)
    {
        if (!IsLiveTailing) return;

        foreach (var record in events)
        {
            _rows.Insert(0, new ConnectionEventRow(record));
        }

        while (_rows.Count > MaxRows) _rows.RemoveAt(_rows.Count - 1);
    }

    [RelayCommand]
    public async Task LoadHistoryAsync()
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();
            var history = await repo.GetRecentConnectionEventsAsync(2_000, CancellationToken.None);

            _rows.Clear();
            foreach (var record in history) _rows.Add(new ConnectionEventRow(record));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load connection history.");
        }
    }

    [RelayCommand]
    public void ClearView() => _rows.Clear();

    [RelayCommand]
    private void ToggleHelp() => ShowHelp = !ShowHelp;

    partial void OnShowHelpChanged(bool value)
    {
        _preferences.Current.ShowConnectionsHelp = value;
        _preferences.Save();
    }
}
