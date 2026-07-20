using Microsoft.Extensions.Configuration;

namespace AeroDB.Sable.Configuration;

/// <summary>
/// Read-only .NET configuration provider backed by an encrypted SurrealKV snapshot.
/// </summary>
public sealed class SableConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly ISableConfigurationStore _store;
    private bool _disposed;

    internal SableConfigurationProvider(ISableConfigurationStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public override void Load()
    {
        ThrowIfDisposed();
        Data = Task.Run(() => _store.LoadAsync(CancellationToken.None))
            .GetAwaiter()
            .GetResult()
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reloads the persistent snapshot and triggers a configuration change token
    /// only when at least one value was added, changed, or removed.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var replacement = loaded.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

        if (AreEqual(Data, replacement))
            return false;

        Data = replacement;
        OnReload();
        return true;
    }

    /// <inheritdoc />
    public override void Set(string key, string? value) =>
        throw new SableConfigurationReadOnlyException();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _store.Dispose();
    }

    private static bool AreEqual(
        IDictionary<string, string?> current,
        IDictionary<string, string?> replacement)
    {
        if (current.Count != replacement.Count)
            return false;

        foreach (var pair in current)
        {
            if (!replacement.TryGetValue(pair.Key, out var other)
                || !string.Equals(pair.Value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
