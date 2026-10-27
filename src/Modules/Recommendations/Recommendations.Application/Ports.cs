using Recommendations.Domain;

namespace Recommendations.Application;

/// <summary>The module's name, which keys its handlers.</summary>
public static class ModuleName
{
    public const string Value = "Recommendations";
}

/// <summary>Writes the derived graph. Every method may run twice or out of order with no harm.</summary>
public interface IGraphProjection
{
    /// <summary>Creates the account or updates its handle; never creates it twice.</summary>
    Task UpsertAccount(Guid accountId, string handle, CancellationToken cancellationToken);

    Task Deactivate(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the change only if its version is higher than the edge's. True when it was applied.
    /// Fails if either account is not in the graph yet: delivery retries it once it is.
    /// </summary>
    Task<bool> Apply(EdgeChange change, CancellationToken cancellationToken);
}

/// <summary>Walks the derived graph.</summary>
public interface IGraphReads
{
    /// <summary>Friends of friends: accounts the reader's intermediates follow, with how many lead to each.</summary>
    Task<IReadOnlyList<Suggestion>> WhoToFollow(Guid reader, TraversalLimits limits, CancellationToken cancellationToken);
}
