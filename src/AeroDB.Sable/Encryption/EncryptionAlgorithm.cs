namespace AeroDB.Sable;

/// <summary>
/// Reversible authenticated-encryption algorithms supported by Sable envelopes.
/// </summary>
public enum EncryptionAlgorithm
{
    /// <summary>AES with a 256-bit key in Galois/Counter Mode.</summary>
    Aes256Gcm = 1,

    /// <summary>
    /// ChaCha20 with Poly1305 authentication and a 256-bit key.
    /// </summary>
    ChaCha20Poly1305 = 2
}

/// <summary>Key-wrapping algorithms used by an encrypted envelope.</summary>
public enum KeyWrappingAlgorithm
{
    /// <summary>
    /// Sable-owned AES-256-GCM wrapping format. This is not a JWE
    /// <c>A256GCMKW</c> serialization.
    /// </summary>
    SableAes256Gcm = 1
}
