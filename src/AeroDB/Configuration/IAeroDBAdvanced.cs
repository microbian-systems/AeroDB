using SurrealDb.Net;

namespace AeroDB;

public interface IAeroDBAdvanced
{
    /// <summary>Direct access to the underlying SurrealDB client.</summary>
    ISurrealDbClient Client { get; }

    /// <summary>Query diagnostics — preview generated SurrealQL and explain plans.</summary>
    IDiagnostics Diagnostics { get; }

    /// <summary>Schema comparison between configured mappings and live database.</summary>
    Task<SchemaDiff> ComputeSchemaDiffAsync(CancellationToken ct = default);

    /// <summary>Create a new SurrealDB session (for advanced query scenarios).</summary>
    Task<ISurrealDbSession> CreateSessionAsync(CancellationToken ct = default);

    /// <summary>Deletes all data from all AeroDB-managed tables. For test/CI use only.</summary>
    Task ResetAllDataAsync(CancellationToken ct = default);

    /// <summary>Permanently delete all documents of type T.</summary>
    Task DeleteAllDocumentsAsync<T>(CancellationToken ct = default) where T : class;

    /// <summary>Permanently delete all documents of a specific type.</summary>
    Task DeleteDocumentsByTypeAsync(Type documentType, CancellationToken ct = default);

    /// <summary>Delete all documents except those of the specified types.</summary>
    Task DeleteDocumentsExceptAsync(Type[] preservedTypes, CancellationToken ct = default);

    /// <summary>Delete all event data. Keeps schema/metadata intact.</summary>
    Task DeleteAllEventDataAsync(CancellationToken ct = default);

    /// <summary>Completely remove a document type including its table and schema.</summary>
    Task CompletelyRemoveAsync(Type documentType, CancellationToken ct = default);
}
