using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Stars.Application.Configuration;
using Stars.Infrastructure.Bootstrap;

namespace Stars.Infrastructure.Persistence;

public static class DatabaseInitialization
{
    public static async Task InitializeDatabaseAsync(this IServiceProvider services)
    {
        var starsOptions = services.GetRequiredService<IOptions<StarsOptions>>().Value;
        if (!starsOptions.AutoMigrate)
        {
            return;
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Stars.DatabaseInitialization");
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StarsDbContext>();
                await db.Database.MigrateAsync();

                var bootstrap = scope.ServiceProvider.GetRequiredService<ProviderBootstrapService>();
                await bootstrap.ApplyAsync(CancellationToken.None);
                return;
            }
            catch (Exception exception) when (attempt < 20 && IsTransient(exception))
            {
                logger.LogWarning(
                    exception,
                    "Database initialization attempt {Attempt} failed; retrying in 2 seconds.",
                    attempt);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    private static bool IsTransient(Exception exception) =>
        exception is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException;
}
