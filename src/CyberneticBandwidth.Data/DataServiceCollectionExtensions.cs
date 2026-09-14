using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CyberneticBandwidth.Core.Abstractions;

namespace CyberneticBandwidth.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddCyberneticBandwidthData(this IServiceCollection services)
    {
        services.AddDbContext<CyberneticBandwidthDbContext>((provider, builder) =>
        {
            var storage = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
            Directory.CreateDirectory(storage.DataDirectory);
            builder.UseSqlite(storage.ConnectionString);
        });

        services.AddScoped<IUsageRepository, UsageRepository>();
        services.AddScoped<INetworkHistoryRepository, NetworkHistoryRepository>();
        services.AddScoped<ISchemaInitializer, SchemaInitializer>();
        return services;
    }
}
