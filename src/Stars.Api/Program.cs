using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Stars.Api.Endpoints;
using Stars.Api.Services;
using Stars.Application.Configuration;
using Stars.Connectors.Abstractions;
using Stars.Connectors.Eva;
using Stars.Connectors.Rest;
using Stars.Infrastructure;
using Stars.Infrastructure.Demo;
using Stars.Infrastructure.Persistence;
using Stars.Infrastructure.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddProblemDetails();
builder.Services.AddCors(options =>
{
    options.AddPolicy("admin", policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("stars-api"))
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation();
        metrics.AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            metrics.AddOtlpExporter();
        }
    });

// Connector composition lives in the host: Core and Infrastructure never reference a
// concrete provider. Adding a connector means registering one IConnector implementation.
builder.Services.AddSingleton<IConnector, FixtureRestConnector>();
builder.Services.AddSingleton<IConnector, EvaDataHubConnector>();
builder.Services.AddSingleton<IConnectorRegistry, ConnectorRegistry>();

var app = builder.Build();

var starsOptions = app.Services.GetRequiredService<IOptions<StarsOptions>>().Value;
if (starsOptions.AutoMigrate)
{
    await ApplyDatabaseAsync(app.Services, starsOptions);
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("admin");

app.MapStarsEndpoints();
app.MapGrpcService<RegistryGrpcService>();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "stars" }));

app.MapGet("/health/ready", async (StarsDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        return await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "ready", database = "postgres" })
            : Results.Problem("PostgreSQL is not reachable", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/health/db", async (StarsDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ok", database = "postgres" })
        : Results.Problem("PostgreSQL is not reachable", statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();

static async Task ApplyDatabaseAsync(IServiceProvider services, StarsOptions starsOptions)
{
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

public partial class Program;
