using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CyberneticBandwidth.App.ViewModels;

public sealed partial class LiveAppRowViewModel(string attributionId, string displayName) : ObservableObject
{
    public string AttributionId { get; } = attributionId;

    [ObservableProperty] private string _displayName = displayName;
    [ObservableProperty] private long _bytesSent;
    [ObservableProperty] private long _bytesReceived;
    [ObservableProperty] private double _recentBytesPerSecond;

    public long Total => BytesSent + BytesReceived;

    public void Add(long sentDelta, long receivedDelta)
    {
        if (sentDelta == 0 && receivedDelta == 0) return;

        BytesSent += sentDelta;
        BytesReceived += receivedDelta;
        OnPropertyChanged(nameof(Total));
    }
}

public sealed partial class SocketRowViewModel(string processName, int pid, BitmapSource? icon) : ObservableObject
{
    public string ProcessName { get; } = processName;
    public int Pid { get; } = pid;
    public BitmapSource? Icon { get; } = icon;

    [ObservableProperty] private int _tcpCount;
    [ObservableProperty] private int _udpCount;
    [ObservableProperty] private int _establishedCount;

    public int Total => TcpCount + UdpCount;
}
