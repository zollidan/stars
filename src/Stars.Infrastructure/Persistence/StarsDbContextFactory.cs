using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Stars.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> so migrations do not need to boot the API host.
/// Override the connection with <c>ConnectionStrings__Stars</c>.
/// </summary>
public class StarsDbContextFactory : IDesignTimeDbContextFactory<StarsDbContext>
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=stars;Username=stars;Password=stars";

    public StarsDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Stars")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__Stars")
            ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<StarsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.ConfigureStars())
            .Options;

        return new StarsDbContext(options);
    }
}
