using Stars.Infrastructure.Registry;
using Stars.Infrastructure.Sync;

namespace Stars.Api.Endpoints;

public sealed record ReviewDecisionRequest(string? Reviewer, string? Reason);

public static class StarsEndpoints
{
    public static IEndpointRouteBuilder MapStarsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/sources", async (RegistryQueryService queries, CancellationToken cancellationToken) =>
            Results.Ok(await queries.ListSourcesAsync(cancellationToken)));

        api.MapPost("/sources/{feedId:guid}/sync", async (
            Guid feedId,
            string? mode,
            SourceSyncService sync,
            CancellationToken cancellationToken) =>
        {
            var result = await sync.SyncFeedAsync(feedId, mode ?? "manual", cancellationToken);
            return result.Busy
                ? Results.Conflict(result)
                : result.Status == "failed"
                    ? Results.Json(result, statusCode: StatusCodes.Status502BadGateway)
                    : Results.Ok(result);
        });

        api.MapPost("/sync", async (SourceSyncService sync, CancellationToken cancellationToken) =>
            Results.Ok(await sync.SyncAllEnabledAsync(cancellationToken)));

        api.MapGet("/directory", async (
            RegistryQueryService queries,
            CancellationToken cancellationToken) =>
            Results.Ok(await queries.ListDirectoryAsync(null, cancellationToken)));

        api.MapGet("/directory/{masterId:guid}", async (
            Guid masterId,
            RegistryQueryService queries,
            CancellationToken cancellationToken) =>
        {
            var master = await queries.GetMasterAsync(masterId, cancellationToken);
            if (master is null)
            {
                return Results.NotFound();
            }

            var reviews = await queries.ListReviewsAsync(masterId, cancellationToken);
            return Results.Ok(new { entity = master, openReviews = reviews });
        });

        api.MapGet("/review", async (
            Guid? masterId,
            RegistryQueryService queries,
            CancellationToken cancellationToken) =>
            Results.Ok(await queries.ListReviewsAsync(masterId, cancellationToken)));

        api.MapPost("/review/{candidateId:guid}/confirm", async (
            Guid candidateId,
            ReviewDecisionRequest? request,
            ReviewService reviews,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var reviewer = string.IsNullOrWhiteSpace(request?.Reviewer) ? "operator" : request.Reviewer;
                var masterId = await reviews.ConfirmAsync(candidateId, reviewer, cancellationToken);
                return Results.Ok(new { masterEntityId = masterId, status = "linked" });
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (LinkConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapPost("/review/{candidateId:guid}/reject", async (
            Guid candidateId,
            ReviewDecisionRequest? request,
            ReviewService reviews,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var reviewer = string.IsNullOrWhiteSpace(request?.Reviewer) ? "operator" : request.Reviewer;
                await reviews.RejectAsync(candidateId, reviewer, request?.Reason, cancellationToken);
                return Results.Ok(new { candidateId, status = "rejected" });
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapPost("/masters/{masterId:guid}/unlink/{sourceEntityId:guid}", async (
            Guid masterId,
            Guid sourceEntityId,
            ReviewDecisionRequest? request,
            ReviewService reviews,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var reviewer = string.IsNullOrWhiteSpace(request?.Reviewer) ? "operator" : request.Reviewer;
                await reviews.UnlinkAsync(masterId, sourceEntityId, reviewer, request?.Reason, cancellationToken);
                return Results.Ok(new { masterEntityId = masterId, sourceEntityId, status = "unlinked" });
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });

        api.MapGet("/resolve", async (
            Guid feedId,
            string kind,
            string sourceKey,
            RegistryQueryService queries,
            CancellationToken cancellationToken) =>
        {
            var resolved = await queries.ResolveAsync(feedId, kind, sourceKey, cancellationToken);
            return resolved is null ? Results.NotFound() : Results.Ok(resolved);
        });

        api.MapGet("/metrics", async (
            RegistryQueryService queries,
            CancellationToken cancellationToken) =>
            Results.Ok(await queries.GetMetricsAsync(cancellationToken)));

        api.MapGet("/providers", (IConfiguration configuration) => Results.Ok(new
        {
            evaAddress = configuration["Stars:Demo:EvaAddress"],
            note = "Provider secrets are referenced by environment variable name, never stored here.",
        }));

        return app;
    }
}
