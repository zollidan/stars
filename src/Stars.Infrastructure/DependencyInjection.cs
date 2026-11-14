using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stars.Infrastructure.Persistence;

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

        services.AddDbContext<StarsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.ConfigureStars();
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
            }));

        return services;
    }
}
