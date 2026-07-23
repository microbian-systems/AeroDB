namespace AeroDB.Sable;

/// <summary>The keyed algorithm used to produce a deterministic blind-index token.</summary>
public enum BlindIndexAlgorithm
{
    /// <summary>HMAC using SHA-256. The full 256-bit output is retained.</summary>
    HmacSha256 = 1
}

/// <summary>Canonicalization applied before a blind-index token is computed.</summary>
public enum BlindIndexNormalizer
{
    /// <summary>
    /// Retains ASCII digits, removes spaces and hyphens, and requires exactly
    /// nine digits. This first normalizer is intended for United States SSNs.
    /// </summary>
    UsSocialSecurityNumberV1 = 1
}

/// <summary>
/// Scope bound into a blind-index token. Record ID is intentionally absent so
/// equal values in the same field produce equal tokens.
/// </summary>
public sealed record BlindIndexContext(
    string? Namespace,
    string? Database,
    string Table,
    string? TenantId,
    string StorageField,
    BlindIndexNormalizer Normalizer);

/// <summary>
/// Produces versioned blind-index tokens without exposing key material to Sable.
/// Implementations must keep blind-index keys separate from encryption keys.
/// </summary>
public interface IBlindIndexTokenProvider
{
    /// <summary>Produces the token stored for a newly written value.</summary>
    ValueTask<string> CreateStorageTokenAsync(
        ReadOnlyMemory<byte> normalizedValue,
        BlindIndexContext context,
        BlindIndexAlgorithm algorithm,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces tokens for every key version that remains searchable, normally
    /// the active key and the immediately previous key.
    /// </summary>
    ValueTask<IReadOnlyList<string>> CreateQueryTokensAsync(
        ReadOnlyMemory<byte> normalizedValue,
        BlindIndexContext context,
        BlindIndexAlgorithm algorithm,
        CancellationToken cancellationToken = default);
}

/// <summary>Fluent configuration for one encrypted field's blind index.</summary>
public sealed class BlindIndexOptions
{
    /// <summary>The keyed token algorithm. Defaults to HMAC-SHA-256.</summary>
    public BlindIndexAlgorithm Algorithm { get; set; } = BlindIndexAlgorithm.HmacSha256;

    /// <summary>The canonicalizer applied before token generation.</summary>
    public BlindIndexNormalizer Normalizer { get; set; } =
        BlindIndexNormalizer.UsSocialSecurityNumberV1;

    /// <summary>
    /// Optional physical sidecar field. The default is the encrypted storage
    /// field name plus <c>_bidx</c>.
    /// </summary>
    public string? StorageFieldName { get; set; }
}
