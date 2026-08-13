namespace AeroDB.Sable;

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

    /// <summary>Documents that were updated in this commit.</summary>
    IReadOnlyList<object> Updated { get; }

    /// <summary>Documents that were inserted in this commit.</summary>
    IReadOnlyList<object> Inserted { get; }

    /// <summary>Documents that were deleted in this commit.</summary>
    IReadOnlyList<object> Deleted { get; }

    /// <summary>Document version fences included in this commit.</summary>
    IReadOnlyList<IDocumentVersionFence> VersionFences
        => Array.Empty<IDocumentVersionFence>();

    /// <summary>Get appended events grouped by stream ID.</summary>
    IReadOnlyList<(string StreamId, IReadOnlyList<object> Events)> GetStreams();

    /// <summary>Create a shallow copy of this change set.</summary>
    IChangeSet Clone();
}

/// <summary>
/// Default implementation of <see cref="IChangeSet"/>.
/// </summary>
internal sealed class ChangeSet : IChangeSet
{
    public IReadOnlyList<Operation> Operations { get; init; } = Array.Empty<Operation>();
    public IReadOnlyList<(string StreamId, object Event)> AppendedEvents { get; init; } = [];
    public IReadOnlyList<object> Updated { get; init; } = Array.Empty<object>();
    public IReadOnlyList<object> Inserted { get; init; } = Array.Empty<object>();
    public IReadOnlyList<object> Deleted { get; init; } = Array.Empty<object>();
    public IReadOnlyList<IDocumentVersionFence> VersionFences { get; init; } = Array.Empty<IDocumentVersionFence>();
    public bool HasChanges => Operations.Count > 0 || AppendedEvents.Count > 0 || VersionFences.Count > 0;

    public IReadOnlyList<(string StreamId, IReadOnlyList<object> Events)> GetStreams()
    {
        return AppendedEvents
            .GroupBy(e => e.StreamId)
            .Select(g => (g.Key, (IReadOnlyList<object>)g.Select(e => e.Event).ToArray()))
            .ToArray();
    }

    public IChangeSet Clone() => new ChangeSet
    {
        Operations = this.Operations,
        Updated = this.Updated,
        Inserted = this.Inserted,
        Deleted = this.Deleted,
        AppendedEvents = this.AppendedEvents,
        VersionFences = this.VersionFences
    };
}
