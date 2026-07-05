namespace AeroDB;

/// <summary>
/// Controls whether tenant IDs are treated as case-sensitive or case-insensitive
/// when matching multi-tenanted documents.
/// </summary>
public enum TenantIdStyle
{
    CaseSensitive,
    CaseInsensitive,
}
