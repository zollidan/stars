using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Stars.Application.Configuration;
using Stars.Connectors.Abstractions;
using Stars.Infrastructure.Demo;
using Stars.Infrastructure.Ingestion;
using Stars.Infrastructure.Jobs;
using Stars.Infrastructure.Matching;
using Stars.Infrastructure.Persistence;
using Stars.Infrastructure.Registry;
using Stars.Infrastructure.Sync;

namespace Stars.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Stars");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'Stars' is not configured. " +
                "Set ConnectionStrings:Stars in appsettings or the ConnectionStrings__Stars environment variable.");
        }

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        services.AddSingleton(dataSource);

        services.AddDbContext<StarsDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql =>
            {
                npgsql.ConfigureStars();
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
            }));

        services.AddOptions<StarsOptions>()
            .Bind(configuration.GetSection(StarsOptions.SectionName))
            .Validate(options => options.MaxRecordsPerSnapshot > 0, "MaxRecordsPerSnapshot must be positive.")
            .ValidateOnStart();
        services.AddSingleton<ISecretResolver, EnvironmentSecretResolver>();
        services.AddHttpClient("connectors", client => client.Timeout = TimeSpan.FromSeconds(45));

        services.AddSingleton<SourceIngestionStore>();
        services.AddScoped<SourceSyncService>();
        services.AddSingleton<MatchingProcessor>();
        services.AddSingleton<RegistryQueryService>();
        services.AddSingleton<ReviewService>();
        services.AddScoped<DemoSeeder>();

        services.AddHostedService<ProcessingJobWorker>();

        return services;
    }
}
