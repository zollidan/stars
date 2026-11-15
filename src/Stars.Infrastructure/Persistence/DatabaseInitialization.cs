using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stars.Application.Configuration;
using Stars.Infrastructure.Demo;
using Stars.Infrastructure.Sync;

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

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StarsDbContext>();
                await db.Database.MigrateAsync();
                var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();
                await seeder.SeedAsync(CancellationToken.None);

                // Complete the first demo import before the worker starts. This removes the
                // startup race where the first provider's jobs run before the second provider
                // has produced any candidates. Manual syncs remain fully asynchronous.
                if (starsOptions.Demo is { Enabled: true, AutoSync: true }
                    && !await db.SourceEntities.AnyAsync())
                {
                    var sync = scope.ServiceProvider.GetRequiredService<SourceSyncService>();
                    await sync.SyncAllEnabledAsync(CancellationToken.None);
                }

                return;
            }
            catch (Exception) when (attempt < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}
