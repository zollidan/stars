using Stars.Api.Services;
using Stars.Infrastructure;
using Stars.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapGrpcService<GreeterService>();

app.MapGet("/health/db", async (StarsDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ok", database = "postgres" })
        : Results.Problem("PostgreSQL is not reachable", statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();
