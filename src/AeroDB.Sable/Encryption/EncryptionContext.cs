namespace AeroDB.Sable;

/// <summary>
/// Stable record context authenticated with an encrypted field.
/// </summary>
public sealed record EncryptionContext(
    string? Namespace,
    string? Database,
    string Table,
    string RecordId,
    string? TenantId,
    string StorageField,
    string CodecId)
{
    /// <summary>
    /// Cryptographic domain for this protected value. The default preserves the
    /// original field-encryption AAD format.
    /// </summary>
    public EncryptionPurpose Purpose { get; init; } = EncryptionPurpose.Field;

    /// <summary>
    /// Optional purpose-specific canonical binding. It is omitted from the
    /// original field-encryption AAD format when not configured.
    /// </summary>
    public string? AuthenticatedContext { get; init; }
}

/// <summary>Separates otherwise similar envelopes into distinct authenticated domains.</summary>
public enum EncryptionPurpose
{
    Field = 0,
    VaultSecret = 1
}

/// <summary>Context supplied when wrapping or unwrapping a field DEK.</summary>
public sealed record KeyWrappingContext(
    EncryptionContext EncryptionContext,
    EncryptionAlgorithm PayloadAlgorithm);
