namespace AeroDB.Sable;

/// <summary>
/// Routes unwrap operations by immutable provider/key metadata while delegating
/// all new wraps to one active provider.
/// </summary>
public sealed class KeyRingWrappingProvider : IKeyWrappingProvider
{
    private readonly IKeyWrappingProvider _active;
    private readonly Dictionary<KeyRoute, IKeyWrappingProvider> _readers = [];

    public KeyRingWrappingProvider(
        IKeyWrappingProvider active,
        params IKeyWrappingProvider[] previous)
    {
        _active = active ?? throw new ArgumentNullException(nameof(active));
        Add(active);
        foreach (var provider in previous ?? [])
            Add(provider);
    }

    public string ProviderId => _active.ProviderId;
    public string KeyId => _active.KeyId;
    public KeyWrappingAlgorithm Algorithm => _active.Algorithm;

    public ValueTask<WrappedKey> WrapAsync(
        ReadOnlyMemory<byte> keyMaterial,
        KeyWrappingContext context,
        CancellationToken cancellationToken = default) =>
        _active.WrapAsync(keyMaterial, context, cancellationToken);

    public ValueTask<OwnedKeyMaterial> UnwrapAsync(
        WrappedKey wrappedKey,
        KeyWrappingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrappedKey);
        var route = new KeyRoute(
            wrappedKey.Algorithm,
            wrappedKey.ProviderId,
            wrappedKey.KeyId);
        if (!_readers.TryGetValue(route, out var provider))
        {
            throw new SableEnvelopeException(
                $"No key-wrapping provider is registered for provider " +
                $"'{wrappedKey.ProviderId}' and key '{wrappedKey.KeyId}'.");
        }

        return provider.UnwrapAsync(wrappedKey, context, cancellationToken);
    }

    private void Add(IKeyWrappingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var route = new KeyRoute(provider.Algorithm, provider.ProviderId, provider.KeyId);
        if (!_readers.TryAdd(route, provider))
        {
            throw new ArgumentException(
                $"Duplicate key-wrapping route '{provider.ProviderId}/{provider.KeyId}'.",
                nameof(provider));
        }
    }

    private readonly record struct KeyRoute(
        KeyWrappingAlgorithm Algorithm,
        string ProviderId,
        string KeyId);
}
