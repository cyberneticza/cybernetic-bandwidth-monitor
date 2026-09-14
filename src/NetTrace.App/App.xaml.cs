using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTrace.App.Services;
using NetTrace.App.Views;
using NetTrace.Core.Services;
using NetTrace.Data;

namespace NetTrace.App;

public partial class App : Application
{
    /// <summary>Passed by a restart: wait for that process to exit before taking its place.</summary>
    public const string AfterArgument = "--after";

    private const string SingleInstanceName = @"Local\NetTrace.SingleInstance";
    private const string ActivateEventName = @"Local\NetTrace.Activate";
    private const int ASFW_ANY = -1;

    private IHost? _host;
    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activateWait;
    private TrayIcon? _tray;

    /// <summary>Set by <see cref="Program.Main"/> before the app starts.</summary>
    public static CollectorMode Mode { get; set; } = CollectorMode.InProcess;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WaitForPreviousCopy(e.Args);

        // Two collectors writing the same SQLite file would fight over the watermark.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool isFirst);
        if (!isFirst)
        {
            // Usually launched from the Start Menu while the first copy sits in the tray: bring
            // that one forward instead of telling the user something they cannot act on.
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var running))
            {
                using (running)
                {
                    AllowSetForegroundWindow(ASFW_ANY);
                    running.Set();
                }
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

        builder.AddNetTrace(Mode);
        _host = builder.Build();

        try
        {
            await using var scope = _host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISchemaInitializer>().InitializeAsync();
        }
        catch (Exception ex) when (Mode == CollectorMode.Viewer)
        {
            // The shared folder may not exist or be open to this user yet; pages show what they can.
            _host.Services.GetService<ILogger<App>>()?.LogWarning(ex, "Could not open the service's database.");
        }

        // Everything published through the broker lands on the dispatcher from here on.
        var broker = _host.Services.GetRequiredService<LiveActivityBroker>();
        broker.AttachUiContext(SynchronizationContext.Current!);

        await _host.StartAsync();

        // Before any view model or window exists: they read the chart colours at construction.
        _host.Services.GetRequiredService<ThemeService>().Initialize();

        var shell = _host.Services.GetRequiredService<ShellWindow>();
        _tray = new TrayIcon(shell, broker, _host.Services.GetRequiredService<NotificationCenter>());
        _tray.Show();

        // Nothing else asks for it, and it has to be listening from the first usage sample.
        _host.Services.GetRequiredService<UsageAlertService>();

        // Keeps start-at-sign-in pointing at this copy of the program if it has moved.
        _ = Task.Run(SignInStartup.RefreshIfEnabled);

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate,
            (_, _) => Dispatcher.BeginInvoke(() => _tray?.ShowShell()),
            state: null, millisecondsTimeOutInterval: Timeout.Infinite, executeOnlyOnce: false);

        // Launched by the per-user autostart entry: collect quietly from the tray, no window.
        bool minimised = e.Args.Any(a => a.Equals("--minimised", StringComparison.OrdinalIgnoreCase)
                                      || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!minimised) shell.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Before any await: the process can be gone by the time the host finishes stopping, and
        // an icon that is never removed lingers in the tray until the mouse passes over it.
        _tray?.Dispose();
        _activateWait?.Unregister(null);
        _activate?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// A copy restarting itself (after the service was installed or removed) starts this one
    /// before it exits; without waiting, this one would find the single-instance lock still held.
    /// </summary>
    private static void WaitForPreviousCopy(string[] args)
    {
        int index = Array.FindIndex(args, a => a.Equals(AfterArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid)) return;

        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            previous.WaitForExit(15_000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Services.GetService<ILogger<App>>()?.LogError(e.Exception, "Unhandled UI exception.");

        var storage = _host?.Services.GetService<IOptions<StorageOptions>>()?.Value ?? new StorageOptions();
        MessageBox.Show(
            $"Something went wrong on the UI thread:\n\n{e.Exception.Message}\n\n" +
            $"The details are in {new RunMode(Mode).LogDirectory(storage)}.",
            "NetTrace", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }
}
