namespace AeroDB.Sable;

/// <summary>
/// Typed variant of <see cref="IConfigureAeroDB"/> for store-specific configuration.
/// Mimics Marten's <c>IConfigureMarten&lt;T&gt;</c>.
/// When multiple store types exist, implementations of this interface are applied only
/// to the matching store type (secondary store pattern).
/// </summary>
/// <typeparam name="TStore">The concrete document store type (e.g., <c>IDocumentStore</c> or a derived type).</typeparam>
public interface IConfigureAeroDB<TStore> : IConfigureAeroDB
    where TStore : IDocumentStore
{
}
