using Stars.Application.Registry;
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
            Results.Ok(await queries.ListSourcesAsync(cancellationToken)))
            .WithTags("Sources").WithSummary("List feeds and their latest sync status")
            .Produces<IReadOnlyList<SourceFeedView>>();

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
        })
            .WithTags("Sources").WithSummary("Import one bounded feed snapshot")
            .Produces<SyncFeedResult>()
            .Produces<SyncFeedResult>(StatusCodes.Status409Conflict)
            .Produces<SyncFeedResult>(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/sync", async (SourceSyncService sync, CancellationToken cancellationToken) =>
            Results.Ok(await sync.SyncAllEnabledAsync(cancellationToken)))
            .WithTags("Sources").WithSummary("Import snapshots from all enabled feeds")
            .Produces<IReadOnlyList<SyncFeedResult>>();

        api.MapGet("/directory", async (
            RegistryQueryService queries,
            CancellationToken cancellationToken) =>
            Results.Ok(await queries.ListDirectoryAsync(null, cancellationToken)))
            .WithTags("Directory").WithSummary("List canonical teams and their source links")
            .Produces<IReadOnlyList<DirectoryEntryView>>();

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
            return Results.Ok(new MasterDetailView(master, reviews));
        })
            .WithTags("Directory").WithSummary("Get a canonical team and its pending reviews")
            .Produces<MasterDetailView>().Produces(StatusCodes.Status404NotFound);

        api.MapGet("/review", async (
            Guid? masterId,
            RegistryQueryService queries,
            CancellationToken cancellationToken) =>
            Results.Ok(await queries.ListReviewsAsync(masterId, cancellationToken)))
            .WithTags("Review").WithSummary("List pending match candidates with their evidence")
            .Produces<IReadOnlyList<ReviewItemView>>();

        api.MapPost("/review/{candidateId:guid}/confirm", async (
            Guid candidateId,
            ReviewDecisionRequest? request,
            ReviewService reviews,
            CancellationToken cancellationToken) =>
        {
            var reviewer = string.IsNullOrWhiteSpace(request?.Reviewer) ? "operator" : request.Reviewer;
            var masterId = await reviews.ConfirmAsync(candidateId, reviewer, cancellationToken);
            return TypedResults.Ok(new { masterEntityId = masterId, status = "linked" });
        })
            .WithTags("Review").WithSummary("Confirm a candidate and link its source team")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/review/{candidateId:guid}/reject", async (
            Guid candidateId,
            ReviewDecisionRequest? request,
            ReviewService reviews,
            CancellationToken cancellationToken) =>
        {
            var reviewer = string.IsNullOrWhiteSpace(request?.Reviewer) ? "operator" : request.Reviewer;
            await reviews.RejectAsync(candidateId, reviewer, request?.Reason, cancellationToken);
            return TypedResults.Ok(new { candidateId, status = "rejected" });
        })
            .WithTags("Review").WithSummary("Reject a pending candidate")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPost("/masters/{masterId:guid}/unlink/{sourceEntityId:guid}", async (
            Guid masterId,
            Guid sourceEntityId,
            ReviewDecisionRequest? request,
            ReviewService reviews,
            CancellationToken cancellationToken) =>
        {
            var reviewer = string.IsNullOrWhiteSpace(request?.Reviewer) ? "operator" : request.Reviewer;
            await reviews.UnlinkAsync(masterId, sourceEntityId, reviewer, request?.Reason, cancellationToken);
            return TypedResults.Ok(new { masterEntityId = masterId, sourceEntityId, status = "unlinked" });
        })
            .WithTags("Review").WithSummary("Close an active link while retaining its audit trail")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/resolve", async (
            Guid feedId,
            string kind,
            string sourceKey,
            RegistryQueryService queries,
            CancellationToken cancellationToken) =>
        {
            var resolved = await queries.ResolveAsync(feedId, kind, sourceKey, cancellationToken);
            return resolved is null ? Results.NotFound() : Results.Ok(resolved);
        })
            .WithTags("Directory").WithSummary("Resolve a source identity to its canonical team")
            .Produces<ResolveView>().Produces(StatusCodes.Status404NotFound);

        api.MapGet("/metrics", async (
            RegistryQueryService queries,
            CancellationToken cancellationToken) =>
            Results.Ok(await queries.GetMetricsAsync(cancellationToken)))
            .WithTags("Diagnostics").WithSummary("Get registry, queue and sync counters")
            .Produces<MetricsView>();

        return app;
    }
}
