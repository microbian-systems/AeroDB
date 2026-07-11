namespace AeroDB.Sable;

/// <summary>
/// Fluent API for schema configuration that provides a unified entry point
/// for document, event stream, and projection configuration.
/// Created during <see cref="DocumentStore.InitializeAsync"/> and passed to
/// <see cref="IConfigureAeroDB.Configure(IAeroSchemaBuilder)"/>.
/// </summary>
public interface IAeroSchemaBuilder
{
    /// <summary>
    /// Fluent API for document-level schema configuration (indices, tenancy, soft-delete,
    /// change tracking, patch events).
    /// Creates or returns a cached <see cref="DocumentMapping{T}"/> for the specified type.
    /// </summary>
    /// <param name="configure">Optional action to configure the document mapping inline.</param>
    /// <typeparam name="T">The document entity type.</typeparam>
    /// <returns>The <see cref="DocumentMapping{T}"/> for the specified type.</returns>
    DocumentMapping<T> For<T>(Action<DocumentMapping<T>>? configure = null) where T : class;

    /// <summary>
    /// Configure a typed domain event stream (append-only, versioned, optimistic concurrency).
    /// Returns a placeholder that will be fleshed out in Phase 2.
    /// </summary>
    /// <typeparam name="T">The event type (a class/record that implements the event contract).</typeparam>
    /// <returns>An <see cref="IEventStreamConfiguration"/> for the event type.</returns>
    IEventStreamConfiguration EventStream<T>() where T : class;

    /// <summary>
    /// Configure a source-generated projection with convention-based Apply(TEvent e) dispatch.
    /// Returns a placeholder that will be fleshed out in Phase 4.
    /// </summary>
    /// <typeparam name="TProjection">The projection type.</typeparam>
    /// <returns>An <see cref="IProjectionConfiguration"/> for the projection type.</returns>
    IProjectionConfiguration Projection<TProjection>() where TProjection : class;

    /// <summary>
    /// Global policy system for configuring all document mappings.
    /// Use <c>Policies.ForAllDocuments()</c> to apply conventions across all mappings,
    /// or <c>Policies.ForDocumentsOfType&lt;T&gt;()</c> for type-specific policies.
    /// </summary>
    DocumentPolicies Policies { get; }
}
