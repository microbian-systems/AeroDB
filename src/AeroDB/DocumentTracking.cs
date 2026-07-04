namespace AeroDB;

/// <summary>Controls the document tracking behavior for a session.</summary>
public enum DocumentTracking
{
    /// <summary>No identity map or change tracking. Each LoadAsync&lt;T&gt; hits the database. Lowest overhead.</summary>
    None = 0,

    /// <summary>Maintains an identity map. LoadAsync&lt;T&gt; returns the same instance on repeated calls for the same ID. Foundation for DirtyTracking.</summary>
    IdentityOnly = 1,

    /// <summary>Identity map + auto-detect modifications on SaveChangesAsync. Not yet implemented.</summary>
    DirtyTracking = 2
}
