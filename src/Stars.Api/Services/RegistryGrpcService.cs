using Grpc.Core;
using Stars.Api.Grpc;
using Stars.Infrastructure.Registry;

namespace Stars.Api.Services;

public sealed class RegistryGrpcService(RegistryQueryService queries) : StarsRegistry.StarsRegistryBase
{
    public override async Task<ResolveBySourceIdResponse> ResolveBySourceId(
        ResolveBySourceIdRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.SourceFeedId, out var feedId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "source_feed_id must be a UUID."));
        }

        var resolved = await queries.ResolveAsync(
            feedId,
            request.EntityKind,
            request.SourceKey,
            context.CancellationToken);

        if (resolved is null)
        {
            return new ResolveBySourceIdResponse { Found = false };
        }

        return new ResolveBySourceIdResponse
        {
            Found = true,
            SourceEntityId = resolved.SourceEntityId.ToString(),
            MasterEntityId = resolved.MasterEntityId.ToString(),
            MasterName = resolved.MasterName,
            DisplayName = resolved.DisplayName,
            Confidence = resolved.Confidence is null ? 0 : (double)resolved.Confidence.Value,
            LinkedAt = resolved.LinkedAt.ToString("O"),
        };
    }

    public override async Task<GetMasterEntityResponse> GetMasterEntity(
        GetMasterEntityRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.MasterEntityId, out var masterId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "master_entity_id must be a UUID."));
        }

        var master = await queries.GetMasterAsync(masterId, context.CancellationToken);
        if (master is null)
        {
            return new GetMasterEntityResponse { Found = false };
        }

        var response = new GetMasterEntityResponse
        {
            Found = true,
            MasterEntityId = master.MasterEntityId.ToString(),
            EntityKind = master.EntityKind,
            PreferredName = master.PreferredName,
            NormalizedName = master.NormalizedName,
            SportCode = master.SportCode ?? string.Empty,
            CountryCode = master.CountryCode ?? string.Empty,
        };
        response.Names.AddRange(master.Names);
        response.Sources.AddRange(master.Sources.Select(source => new SourceLink
        {
            SourceEntityId = source.SourceEntityId.ToString(),
            SourceFeedId = source.SourceFeedId.ToString(),
            FeedKey = source.FeedKey,
            ProviderName = source.ProviderName,
            DisplayName = source.DisplayName,
            ExternalId = source.ExternalId ?? string.Empty,
            Names = { source.Names },
        }));

        return response;
    }

    public override async Task<ListMatchesForReviewResponse> ListMatchesForReview(
        ListMatchesForReviewRequest request,
        ServerCallContext context)
    {
        var limit = request.Limit <= 0 ? 100 : Math.Min(request.Limit, 250);
        var items = await queries.ListReviewsAsync(null, context.CancellationToken);
        var response = new ListMatchesForReviewResponse();
        response.Items.AddRange(items.Take(limit).Select(item => new ReviewItem
        {
            CandidateId = item.CandidateId.ToString(),
            SourceEntityId = item.SourceEntityId.ToString(),
            SourceName = item.SourceName,
            SourceFeedKey = item.SourceFeedKey,
            TargetName = item.TargetName,
            Score = item.Score ?? 0,
            Reason = item.Evidence.FirstOrDefault()?.Explanation ?? "pending_review",
            GeneratedAt = item.GeneratedAt.ToString("O"),
        }));

        return response;
    }
}
