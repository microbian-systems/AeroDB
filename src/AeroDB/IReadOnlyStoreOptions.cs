namespace AeroDB;

/// <summary>Read-only view of StoreOptions for metadata consumers.</summary>
public interface IReadOnlyStoreOptions
{
    string Endpoint { get; }
    string? Namespace { get; }
    string? Database { get; }
    string? Username { get; }
    string? Password { get; }
    string? Token { get; }
    TenancyStyle TenancyStyle { get; }
    string? DefaultTenantId { get; }
    DocumentTracking Tracking { get; }
    bool UseOptimisticConcurrency { get; }
    int UpdateBatchSize { get; }
    int? CommandTimeout { get; }
    IReadOnlyList<IProjection> Projections { get; }
    IReadOnlyList<IDocumentSessionListener> Listeners { get; }
    IReadOnlyList<IChangeListener> ChangeListeners { get; }
    SchemaOptions Schema { get; }
    EventSourcingOptions Events { get; }
}
