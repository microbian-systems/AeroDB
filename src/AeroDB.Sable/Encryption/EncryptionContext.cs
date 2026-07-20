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
    string CodecId);

/// <summary>Context supplied when wrapping or unwrapping a field DEK.</summary>
public sealed record KeyWrappingContext(
    EncryptionContext EncryptionContext,
    EncryptionAlgorithm PayloadAlgorithm);
