namespace AeroDB.Sable;

/// <summary>
/// Marks a <see cref="string"/> or <see cref="byte"/> array property for
/// randomized, reversible application-layer encryption.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class EncryptAttribute : Attribute
{
    /// <summary>Creates an AES-256-GCM field mapping.</summary>
    public EncryptAttribute()
        : this(EncryptionAlgorithm.Aes256Gcm)
    {
    }

    /// <summary>Creates a field mapping for the selected reversible algorithm.</summary>
    public EncryptAttribute(EncryptionAlgorithm algorithm)
    {
        Algorithm = algorithm;
    }

    /// <summary>The reversible encryption algorithm selected for this field.</summary>
    public EncryptionAlgorithm Algorithm { get; }
}
