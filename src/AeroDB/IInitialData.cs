namespace AeroDB;

/// <summary>
/// Populates initial data during <see cref="DocumentStore.InitializeAsync"/>.
/// Register implementations via <see cref="StoreOptions.InitialData"/>.
/// </summary>
public interface IInitialData
{
    /// <summary>
    /// Called during store initialization. Use the session to populate seed data.
    /// The session is a lightweight write session. SaveChangesAsync is called after all
    /// <see cref="IInitialData"/> implementations run.
    /// </summary>
    Task PopulateAsync(IDocumentSession session, CancellationToken ct);
}
