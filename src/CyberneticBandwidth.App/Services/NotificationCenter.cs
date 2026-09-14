namespace CyberneticBandwidth.App.Services;

/// <summary>
/// Where alerts go to be shown. Services raise them here without knowing about the tray icon,
/// which is created after the container and subscribes once it exists.
/// </summary>
public sealed class NotificationCenter
{
    public event Action<string, string>? Raised;

    public void Notify(string title, string message) => Raised?.Invoke(title, message);
}

/// <summary>Lets one page send the shell to another, e.g. Live's connection card to Network.</summary>
public sealed class NavigationRequests
{
    public event Action<Type>? Requested;

    public void NavigateTo<TViewModel>() => Requested?.Invoke(typeof(TViewModel));
}
