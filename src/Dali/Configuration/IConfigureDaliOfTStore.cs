namespace Dali;

/// <summary>
/// Typed variant of <see cref="IConfigureDali"/> for store-specific configuration.
/// Mimics Marten's <c>IConfigureMarten&lt;T&gt;</c>.
/// When multiple store types exist, implementations of this interface are applied only
/// to the matching store type (secondary store pattern).
/// </summary>
/// <typeparam name="TStore">The concrete document store type (e.g., <c>IDocumentStore</c> or a derived type).</typeparam>
public interface IConfigureDali<TStore> : IConfigureDali
    where TStore : IDocumentStore
{
}
