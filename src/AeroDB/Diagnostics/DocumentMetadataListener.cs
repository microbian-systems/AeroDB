using AeroDB.Metadata;

namespace AeroDB.Diagnostics;

/// <summary>
/// Document session listener that auto-populates <see cref="IDocumentMetadata"/> properties
/// (<see cref="IDocumentMetadata.CreatedAt"/>, <see cref="IDocumentMetadata.LastModified"/>,
/// <see cref="IDocumentMetadata.LastModifiedBy"/>) during store operations.
///
/// <para>
/// Register via <c>StoreOptions.Listeners.Add(new DocumentMetadataListener())</c>
/// or use the convenience extension method <c>o.EnableDocumentMetadata()</c>.
/// </para>
/// </summary>
public class DocumentMetadataListener : DocumentSessionListenerBase
{
    /// <summary>
    /// Called before each entity is stored. Sets audit metadata on entities
    /// implementing <see cref="IDocumentMetadata"/>.
    /// </summary>
    public override Task BeforeStoreAsync(IDocumentSession session, object entity, CancellationToken ct)
    {
        if (entity is IDocumentMetadata meta)
        {
            var now = DateTimeOffset.UtcNow;
            if (meta.CreatedAt == default)
                meta.CreatedAt = now;
            meta.LastModified = now;

            // Set LastModifiedBy from the session's CurrentUser
            if (session is InternalSessionBase sb && !string.IsNullOrEmpty(sb.CurrentUser))
                meta.LastModifiedBy = sb.CurrentUser;
        }

        return Task.CompletedTask;
    }
}
