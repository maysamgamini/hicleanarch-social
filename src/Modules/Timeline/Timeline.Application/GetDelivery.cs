using Social.BuildingBlocks.Application;
using Social.BuildingBlocks.Domain;

namespace Timeline.Application;

/// <summary>
/// <c>GET /v1/posts/{id}/delivery</c>: which acknowledgments the post has reached, from Timeline's
/// own records, for its author only. No other module is asked.
/// </summary>
public sealed record GetDelivery(Guid PostId, Guid CallerId) : IQuery<Delivery>;

/// <summary>The author asks right after posting: their own write, so the writer.</summary>
[ReadsTheWriter]
public sealed class GetDeliveryHandler(IFanoutProgress progress) : IQueryHandler<GetDelivery, Delivery>
{
    public static readonly Error NotFound = Error.NotFound("post_not_found", "No post of yours has this id.");

    public async Task<Result<Delivery>> Handle(GetDelivery query, CancellationToken cancellationToken)
    {
        var delivery = await progress.Find(query.PostId, cancellationToken);
        return delivery is null || delivery.AuthorId != query.CallerId
            ? Result.Failure<Delivery>(NotFound)
            : Result.Success(delivery);
    }
}
