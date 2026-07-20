namespace AeroDB.Sable;

/// <summary>
/// Adds an equality-leaking, separately keyed blind index to an
/// <see cref="EncryptAttribute"/> string property.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class BlindIndexAttribute : Attribute
{
    /// <summary>The keyed token algorithm.</summary>
    public BlindIndexAlgorithm Algorithm { get; set; } = BlindIndexAlgorithm.HmacSha256;

    /// <summary>The canonicalizer applied before token generation.</summary>
    public BlindIndexNormalizer Normalizer { get; set; } =
        BlindIndexNormalizer.UsSocialSecurityNumberV1;

    /// <summary>Optional physical sidecar field name.</summary>
    public string? StorageFieldName { get; set; }
}
