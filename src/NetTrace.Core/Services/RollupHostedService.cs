using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTrace.Core.Abstractions;

namespace NetTrace.Core.Services;

/// <summary>
/// Promotes minute buckets to hours and hours to days, then prunes. Without this, a year of
/// minute rows would be roughly half a million per application and every chart would crawl.
/// </summary>
public sealed class RollupHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionOptions> options,
    ILogger<RollupHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Once on startup: the app may have been closed for weeks.
        await RunAsync(ct);

        using var timer = new PeriodicTimer(options.Value.RollupInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await RunAsync(ct);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUsageRepository>();
            await repo.RunRollupAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Rollup failed. Collection is unaffected; the next pass will retry.");
        }
    }
}
