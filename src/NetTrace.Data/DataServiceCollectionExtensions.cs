using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetTrace.Core.Abstractions;

namespace NetTrace.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddNetTraceData(this IServiceCollection services)
    {
        services.AddDbContext<NetTraceDbContext>((provider, builder) =>
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
