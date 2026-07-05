namespace AeroDB;

/// <summary>
/// Marks an entity as soft-deletable. AeroDB uses these properties directly on the entity
/// (unlike Marten which uses shadow columns <c>mt_deleted</c> and <c>mt_deleted_at</c>).
/// Queries on <see cref="ISoftDeleted"/> types automatically exclude entities where <c>Deleted == true</c>.
/// </summary>
public interface ISoftDeleted
{
    /// <summary>
    /// Timestamp indicating when the document was soft-deleted, or null if not deleted.
    /// </summary>
    DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Whether the document is soft-deleted.
    /// </summary>
    bool Deleted { get; set; }
}
