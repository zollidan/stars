using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Scalar.AspNetCore;
using Stars.Api.Endpoints;
using Stars.Api.Services;
using Stars.Connectors.Abstractions;
using Stars.Connectors.Eva;
using Stars.Connectors.Rest;
using Stars.Infrastructure;
using Stars.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("admin", policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<StarsDbContext>("database");

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

await app.Services.InitializeDatabaseAsync();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("admin");

app.MapStarsEndpoints();
app.MapGrpcService<RegistryGrpcService>();
app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("STARS HTTP API").DisableDefaultFonts());

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/db");

app.MapGet("/", () => Results.Redirect("/index.html")).ExcludeFromDescription();

app.Run();

public partial class Program;
