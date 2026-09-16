using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.Core.Services;
using CyberneticBandwidth.Data;

namespace CyberneticBandwidth.App;

/// <summary>
/// One program, three ways to run:
///  - <c>--service</c>: the Windows service. No window: collects and serves windows over the collector link.
///  - <c>--install-service</c> / <c>--remove-service</c>: the elevated setup step Settings starts behind a UAC prompt.
///  - <c>--veloapp-*</c>: a hook of the installer (see <see cref="Installer"/>), which runs and exits.
///  - otherwise the window, which collects itself, or shows the service's data when the service is installed.
/// </summary>
public static class Program
{
    public const string ServiceArgument = "--service";

    /// <summary>Testing only: run the window as a viewer of a <c>--service</c> started from a console.</summary>
    private const string ViewerArgument = "--viewer";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals(ServiceArgument, StringComparison.OrdinalIgnoreCase)))
            return RunService(args);

        if (CollectorServiceSetup.IsSetupCommand(args))
            return CollectorServiceSetup.RunSetupCommand(args);

        // Exits here when Setup.exe or an update runs this program for one of its hooks.
        Installer.Run();

        App.Mode = args.Any(a => a.Equals(ViewerArgument, StringComparison.OrdinalIgnoreCase)) || CollectorServiceSetup.IsInstalled()
            ? CollectorMode.Viewer
            : CollectorMode.InProcess;

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int RunService(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args.Where(a => !a.Equals(ServiceArgument, StringComparison.OrdinalIgnoreCase)).ToArray(),
            ContentRootPath = AppContext.BaseDirectory
        });

        // Under the Service Control Manager this reports start and stop to Windows; started from a
        // console for testing, it behaves as an ordinary console host.
        builder.Services.AddWindowsService(options => options.ServiceName = CollectorServiceSetup.ServiceName);
        builder.AddCyberneticBandwidth(CollectorMode.Service);

        using var host = builder.Build();
        using (var scope = host.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ISchemaInitializer>().InitializeAsync().GetAwaiter().GetResult();
        }

        host.Run();
        return 0;
    }
}
