using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CyberneticBandwidth.App.Services;
using CyberneticBandwidth.App.ViewModels;
using CyberneticBandwidth.App.Views;
using CyberneticBandwidth.Core.Abstractions;
using CyberneticBandwidth.Core.Services;
using CyberneticBandwidth.Core.Sources;
using CyberneticBandwidth.Data;
using Serilog;
using Serilog.Events;

namespace CyberneticBandwidth.App;

internal static class HostingExtensions
{
    /// <summary>
    /// Everything for one way of running (see <see cref="CollectorMode"/>). The collector (sources,
    /// sampler, watcher, health monitor) runs in the usual app and in the service; a viewer gets its
    /// live figures from the service instead. The window and its pages exist everywhere but the service.
    /// </summary>
    public static HostApplicationBuilder AddCyberneticBandwidth(this HostApplicationBuilder builder, CollectorMode mode)
    {
        var storageSection = builder.Configuration.GetSection(StorageOptions.SectionName);
        bool folderConfigured = !string.IsNullOrWhiteSpace(storageSection[nameof(StorageOptions.DataDirectory)]);

        builder.Services.Configure<StorageOptions>(storageSection);
        // The service and its viewers share one folder, unless appsettings names another.
        if (mode != CollectorMode.InProcess && !folderConfigured)
            builder.Services.PostConfigure<StorageOptions>(o => o.DataDirectory = StorageOptions.SharedDirectory);

        builder.Services.Configure<SamplerOptions>(builder.Configuration.GetSection(SamplerOptions.SectionName));
        builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
        builder.Services.Configure<HealthOptions>(builder.Configuration.GetSection(HealthOptions.SectionName));
        builder.Services.AddSingleton(new RunMode(mode));

        var storage = storageSection.Get<StorageOptions>() ?? new StorageOptions();
        if (mode != CollectorMode.InProcess && !folderConfigured) storage.DataDirectory = StorageOptions.SharedDirectory;
        AddLogging(builder, storage, mode);

        builder.Services.AddCyberneticBandwidthData();
        AddCollector(builder, mode);
        if (mode != CollectorMode.Service) AddWindow(builder, mode);

        return builder;
    }

    private static void AddLogging(HostApplicationBuilder builder, StorageOptions storage, CollectorMode mode)
    {
        // Separate files keep the service's story apart from a window's.
        string directory = new RunMode(mode).LogDirectory(storage);
        string file = mode switch
        {
            CollectorMode.Service => "cyberneticbandwidth-service-.log",
            CollectorMode.Viewer => "cyberneticbandwidth-window-.log",
            _ => "cyberneticbandwidth-.log"
        };

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging simply has nowhere to write; the app works regardless.
        }

        builder.Services.AddSerilog(config => config
            .MinimumLevel.Information()
            // EF Core logs every SQL command at Information: ~25 lines a minute that bury the
            // messages that explain what the collector is doing.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(directory, file),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true));
    }

    private static void AddCollector(HostApplicationBuilder builder, CollectorMode mode)
    {
        builder.Services.AddSingleton<LiveActivityBroker>();
        builder.Services.AddSingleton<AppNameResolver>();

        // Registered in every mode: pages name the source even when the service does the reading.
        builder.Services.AddSingleton<INetworkUsageSource, NduAttributedUsageSource>();

        if (mode == CollectorMode.Service)
        {
            builder.Services.AddSingleton<ServiceSettingsStore>();
            builder.Services.AddSingleton(sp => new MonitoringSettings
            {
                HealthChecksEnabled = sp.GetRequiredService<ServiceSettingsStore>().LoadHealthChecksEnabled()
            });
        }
        else
        {
            builder.Services.AddSingleton(sp => new MonitoringSettings
            {
                HealthChecksEnabled = sp.GetRequiredService<PreferencesStore>().Current.HealthChecksEnabled
            });
        }

        if (mode == CollectorMode.Viewer)
        {
            builder.Services.AddSingleton<CollectorPipeClient>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorPipeClient>());
            return;
        }

        builder.Services.AddSingleton<IProcessResolver, ProcessResolver>();
        builder.Services.AddSingleton<IConnectionSource, TcpUdpConnectionSource>();
        builder.Services.AddSingleton<IThroughputSource, InterfaceThroughputSource>();
        builder.Services.AddSingleton<NetworkEnvironment>();
        builder.Services.AddSingleton<NetworkEventRecorder>();

        // Registration order does not matter — the sampler picks by Priority, highest first.
        builder.Services.AddHostedService<NetworkSamplerService>();
        builder.Services.AddHostedService<RollupHostedService>();
        builder.Services.AddHostedService<NetworkWatcherService>();
        builder.Services.AddHostedService<ConnectionHealthMonitor>();

        if (mode == CollectorMode.Service)
        {
            builder.Services.AddHostedService(sp => new CollectorPipeServer(
                sp.GetRequiredService<LiveActivityBroker>(),
                sp.GetRequiredService<MonitoringSettings>(),
                () => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value.DataDirectory,
                enabled => sp.GetRequiredService<ServiceSettingsStore>().SaveHealthChecksEnabled(enabled),
                sp.GetRequiredService<ILogger<CollectorPipeServer>>()));
        }
    }

    private static void AddWindow(HostApplicationBuilder builder, CollectorMode mode)
    {
        builder.Services.AddSingleton<IconCache>();
        builder.Services.AddSingleton<PreferencesStore>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<NotificationCenter>();
        builder.Services.AddSingleton<NavigationRequests>();
        builder.Services.AddSingleton<UsageAlertService>();
        builder.Services.AddSingleton<SpeedTestService>();

        builder.Services.AddSingleton<ShellWindow>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<LogsViewModel>();
        builder.Services.AddSingleton<GraphsViewModel>();
        builder.Services.AddSingleton<HistoryViewModel>();
        builder.Services.AddSingleton<NetworkViewModel>();
        builder.Services.AddSingleton<SpeedTestViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<AboutViewModel>();
    }
}
