namespace AeroDB.Sable;

/// <summary>Base exception for Sable field-encryption failures.</summary>
public class SableEncryptionException : Exception
{
    public SableEncryptionException(string message)
        : base(message)
    {
    }

    public SableEncryptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An encrypted document mapping is incomplete or internally conflicting.</summary>
public sealed class SableEncryptionConfigurationException : SableEncryptionException
{
    public SableEncryptionConfigurationException(Type documentType, string problem)
        : base($"Encrypted document type '{documentType.FullName}' has invalid configuration: {problem}")
    {
    }
}

/// <summary>The stored encrypted envelope is malformed or unsupported.</summary>
public sealed class SableEnvelopeException : SableEncryptionException
{
    public SableEnvelopeException(string message)
        : base(message)
    {
    }

    public SableEnvelopeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The envelope authentication tag, wrapping tag, or AAD did not validate.</summary>
public sealed class SableEncryptionAuthenticationException : SableEncryptionException
{
    public SableEncryptionAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An encrypted document does not have the stable ID required by its AAD.</summary>
public sealed class SableEncryptedDocumentRequiresIdException : SableEncryptionException
{
    public SableEncryptedDocumentRequiresIdException(Type documentType)
        : base($"Encrypted document type '{documentType.FullName}' requires an application-assigned record ID before it can be stored.")
    {
    }
}

/// <summary>An operation is not supported for a document containing encrypted fields.</summary>
public sealed class SableEncryptedOperationNotSupportedException : SableEncryptionException
{
    public SableEncryptedOperationNotSupportedException(Type documentType, string operation)
        : base($"Operation '{operation}' is not supported for encrypted document type '{documentType.FullName}' in the current encryption phase.")
    {
    }
}

/// <summary>A blind-index mapping or provider is invalid.</summary>
public sealed class SableBlindIndexConfigurationException : SableEncryptionException
{
    public SableBlindIndexConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>A candidate value cannot be normalized for a blind index.</summary>
public sealed class SableBlindIndexNormalizationException : SableEncryptionException
{
    public SableBlindIndexNormalizationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A blind-index hit did not match the authenticated plaintext after decryption.
/// </summary>
public sealed class SableBlindIndexIntegrityException : SableEncryptionException
{
    public SableBlindIndexIntegrityException(Type documentType, string propertyName)
        : base(
            $"Blind-index lookup for '{documentType.FullName}.{propertyName}' returned a record " +
            "whose decrypted value did not match the requested value. The index is stale, " +
            "misconfigured, or corrupted.")
    {
    }
}

/// <summary>A document changed while an active-key blind-index token was being written.</summary>
public sealed class SableBlindIndexReindexConflictException : SableEncryptionException
{
    public SableBlindIndexReindexConflictException(Type documentType)
        : base(
            $"Blind-index reindexing for '{documentType.FullName}' detected a concurrent " +
            "protected-field update. Retry the affected maintenance batch.")
    {
    }
}
