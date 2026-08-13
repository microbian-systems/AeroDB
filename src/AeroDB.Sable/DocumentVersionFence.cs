using SurrealDb.Net.Models;

namespace AeroDB.Sable;

/// <summary>
/// Describes the lifecycle of a document version fence queued on a document session.
/// </summary>
public enum VersionFenceStatus
{
    /// <summary>The fence is queued and has not yet executed.</summary>
    Queued,

    /// <summary>The conditional version increment succeeded inside an explicit transaction.</summary>
    Applied,

    /// <summary>The transaction containing the fence committed successfully.</summary>
    Committed,

    /// <summary>The fence or its containing transaction failed before commit.</summary>
    Failed,

    /// <summary>The transaction containing the fence was rolled back.</summary>
    RolledBack
}

/// <summary>
/// A conditional, mutating optimistic-concurrency fence for a single document.
/// The committed version is deliberately unavailable until the containing
/// transaction has committed successfully.
/// </summary>
public interface IDocumentVersionFence
{
    /// <summary>The mapped document type protected by the fence.</summary>
    Type DocumentType { get; }

    /// <summary>The exact SurrealDB record protected by the fence.</summary>
    RecordId RecordId { get; }

    /// <summary>The version that must exist when the fence executes.</summary>
    long ExpectedVersion { get; }

    /// <summary>The current lifecycle status of the fence.</summary>
    VersionFenceStatus Status { get; }

    /// <summary>
    /// The newly committed storage version, or <see langword="null"/> until the
    /// containing transaction commits successfully.
    /// </summary>
    long? CommittedVersion { get; }
}

internal sealed class DocumentVersionFence : IDocumentVersionFence
{
    private long? _appliedVersion;

    public DocumentVersionFence(
        Type documentType,
        RecordId recordId,
        long expectedVersion,
        string? database,
        string identityKey,
        string versionField,
        string? tenantField,
        string? tenantId)
    {
        DocumentType = documentType;
        RecordId = recordId;
        ExpectedVersion = expectedVersion;
        Database = database;
        IdentityKey = identityKey;
        VersionField = versionField;
        TenantField = tenantField;
        TenantId = tenantId;
    }

    public Type DocumentType { get; }
    public RecordId RecordId { get; }
    public long ExpectedVersion { get; }
    public VersionFenceStatus Status { get; private set; } = VersionFenceStatus.Queued;
    public long? CommittedVersion { get; private set; }

    internal string? Database { get; }
    internal string IdentityKey { get; }
    internal string VersionField { get; }
    internal string? TenantField { get; }
    internal string? TenantId { get; }
    internal object? TrackedDocument { get; private set; }

    internal bool Targets(
        RecordId recordId,
        string? database)
        => string.Equals(Database, database, StringComparison.Ordinal)
           && RecordId.Equals(recordId);

    internal void AttachTrackedDocument(object document)
    {
        ArgumentNullException.ThrowIfNull(document);
        TrackedDocument ??= document;
    }

    internal void MarkApplied(long version)
    {
        if (Status != VersionFenceStatus.Queued)
            throw new InvalidOperationException($"Cannot apply a version fence in status '{Status}'.");

        _appliedVersion = version;
        Status = VersionFenceStatus.Applied;
    }

    internal void MarkCommitted()
    {
        if (Status != VersionFenceStatus.Applied || _appliedVersion is null)
            throw new InvalidOperationException($"Cannot commit a version fence in status '{Status}'.");

        CommittedVersion = _appliedVersion;
        Status = VersionFenceStatus.Committed;
    }

    internal void MarkFailed()
    {
        if (Status is VersionFenceStatus.Committed or VersionFenceStatus.RolledBack)
            return;

        CommittedVersion = null;
        Status = VersionFenceStatus.Failed;
    }

    internal void MarkRolledBack()
    {
        if (Status == VersionFenceStatus.Committed)
            throw new InvalidOperationException("A committed version fence cannot be rolled back.");

        _appliedVersion = null;
        CommittedVersion = null;
        Status = VersionFenceStatus.RolledBack;
    }
}
