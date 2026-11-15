using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Stars.Tests;

public class HealthChecksTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, HttpStatusCode.OK)]
    [InlineData(HealthStatus.Unhealthy, HttpStatusCode.ServiceUnavailable)]
    public async Task Liveness_ignores_database_while_readiness_checks_it(
        HealthStatus databaseStatus, HttpStatusCode expectedStatus)
    {
        var databaseChecks = 0;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Stars", "Host=localhost;Database=stars;Username=stars");
            builder.UseSetting("Stars:AutoMigrate", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.PostConfigure<HealthCheckServiceOptions>(options =>
                {
                    Assert.Contains(options.Registrations, check => check.Name == "database");
                    options.Registrations.Clear();
                    options.Registrations.Add(new HealthCheckRegistration(
                        "database", _ => new DatabaseCheck(() =>
                        {
                            databaseChecks++;
                            return new HealthCheckResult(databaseStatus);
                        }), null, null));
                });
            });
        });
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(0, databaseChecks);

        foreach (var path in new[] { "/health/ready", "/health/db" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(expectedStatus, response.StatusCode);
            Assert.Equal(databaseStatus.ToString(), await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(2, databaseChecks);
    }

    private sealed class DatabaseCheck(Func<HealthCheckResult> check) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(check());
    }
}
