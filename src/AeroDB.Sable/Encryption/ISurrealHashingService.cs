namespace AeroDB.Sable;

/// <summary>Explicit one-way hashing backed by the connected SurrealDB server.</summary>
public interface ISurrealHashingService
{
    Task<SurrealPasswordHash> GenerateAsync(
        string plaintext,
        SurrealHashAlgorithm algorithm,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(
        string candidate,
        SurrealPasswordHash passwordHash,
        CancellationToken cancellationToken = default);

    Task<string> DigestAsync(
        string value,
        SurrealHashAlgorithm algorithm,
        CancellationToken cancellationToken = default);

    Task<SurrealHashCapabilities> ProbeCapabilitiesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>An encoded password hash coupled to the algorithm that produced it.</summary>
public sealed record SurrealPasswordHash(
    SurrealHashAlgorithm Algorithm,
    string EncodedHash);

/// <summary>Algorithms accepted or rejected by the connected SurrealDB server.</summary>
public sealed record SurrealHashCapabilities(
    IReadOnlyList<SurrealHashAlgorithm> Supported,
    IReadOnlyList<SurrealHashAlgorithm> Unsupported)
{
    public bool Supports(SurrealHashAlgorithm algorithm) => Supported.Contains(algorithm);
}
