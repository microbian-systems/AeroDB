namespace Dali;

/// <summary>
/// Summary of changes committed during a single <c>SaveChangesAsync</c> call.
/// Passed to <see cref="IDocumentSessionListener.AfterCommitAsync"/>.
/// </summary>
public interface IChangeSet
{
    /// <summary>All operations in this unit of work (Added, Modified, Deleted, SoftDeleted).</summary>
    IReadOnlyList<Operation> Operations { get; }

    /// <summary>Whether any changes were committed.</summary>
    bool HasChanges { get; }

    /// <summary>Events appended during this SaveChanges, grouped by stream.</summary>
    IReadOnlyList<(string StreamId, object Event)> AppendedEvents { get; }
}

/// <summary>
/// Default implementation of <see cref="IChangeSet"/>.
/// </summary>
internal sealed class ChangeSet : IChangeSet
{
    public IReadOnlyList<Operation> Operations { get; init; } = Array.Empty<Operation>();
    public IReadOnlyList<(string StreamId, object Event)> AppendedEvents { get; init; } = [];
    public bool HasChanges => Operations.Count > 0 || AppendedEvents.Count > 0;
}
