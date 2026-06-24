namespace Dali;

/// <summary>
/// Extension methods providing a <see cref="Guid"/> overload for <see cref="IQuerySession.FetchLatest{T}(string, CancellationToken)"/>.
/// </summary>
public static class FetchLatestExtensions
{
    /// <summary>
    /// Fetch the latest projected aggregate document for the given stream
    /// without replaying events. Returns null if no projected document exists.
    /// </summary>
    public static Task<T?> FetchLatest<T>(this IQuerySession session, Guid streamId, CancellationToken ct = default)
        where T : class
        => session.FetchLatest<T>(streamId.ToString("D"), ct);
}
