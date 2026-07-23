namespace AeroDB.Sable.Configuration;

/// <summary>One decrypted configuration entry.</summary>
public sealed record SableConfigurationEntry(
    string Key,
    string Value,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Asynchronous provisioning and administration API for the encrypted
/// embedded configuration store.
/// </summary>
public interface ISableConfigurationStore : IDisposable
{
    /// <summary>Gets one entry using case-insensitive key semantics.</summary>
    Task<SableConfigurationEntry?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>Loads and decrypts the complete configuration snapshot.</summary>
    Task<IReadOnlyDictionary<string, string?>> LoadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Encrypts and idempotently stores a value.</summary>
    Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an entry. Returns false when it did not exist.</summary>
    Task<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default);
}
