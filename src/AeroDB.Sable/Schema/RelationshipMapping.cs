namespace AeroDB.Sable;

public enum RelationshipKind
{
    HasOne,
    HasMany
}

public enum RelationshipStorageModel
{
    RecordLink,
    RecordLinkArray,
    ScalarForeignKey,
    GraphEdge
}

public enum RelationshipStorageKind
{
    ScalarForeignKey,
    RecordLink
}

public enum RelationshipCardinality
{
    One,
    Many
}

public enum RelationshipOrigin
{
    Explicit,
    Convention,
    SourceGenerated
}

public sealed record RelationshipConstraints(
    bool Reference = false,
    RelationshipOnDeleteAction? OnDelete = null,
    string? OnDeleteThenSurql = null,
    bool Unique = false);

public sealed record RelationshipCandidate(
    string SourceTypeName,
    string TargetTypeName,
    string SourceMemberName,
    string TargetIdMemberName,
    RelationshipStorageKind StorageKind,
    RelationshipCardinality Cardinality,
    bool IsTentative = false);

public sealed record RelationshipDescriptor(
    Type SourceType,
    Type TargetType,
    string SourceTableName,
    string TargetTableName,
    string SourceMemberName,
    string SourceFieldName,
    string TargetIdMemberName,
    string TargetIdFieldName,
    RelationshipStorageKind StorageKind,
    RelationshipCardinality Cardinality,
    bool IsNullable,
    bool IsRequired,
    RelationshipOrigin Origin,
    bool WasOverridden,
    RelationshipConstraints Constraints);

public sealed class RelationshipMapping
{
    public Type SourceType { get; private init; } = default!;
    public string SourceTableName { get; private set; } = "";
    public Type TargetType { get; private init; } = default!;
    public string TargetTableName { get; private init; } = "";
    public string? ClrMemberName { get; private init; }
    public string StorageFieldName { get; private set; } = "";
    public RelationshipKind Kind { get; private init; }
    public RelationshipStorageModel StorageModel { get; private init; }
    public RelationshipStorageKind StorageKind { get; private init; }
    public RelationshipCardinality Cardinality { get; private init; }
    public RelationshipOrigin Origin { get; private init; } = RelationshipOrigin.Explicit;
    public bool WasOverridden { get; private init; }
    public bool Required { get; private set; } = true;
    public bool Nullable { get; private set; }
    public bool Reference { get; private set; }
    public RelationshipOnDeleteAction? OnDelete { get; private set; }
    public string? OnDeleteThenSurql { get; private set; }
    public bool Unique { get; private set; }

    internal static RelationshipMapping Create(
        Type sourceType,
        string sourceTableName,
        Type targetType,
        string targetTableName,
        string? clrMemberName,
        string storageFieldName,
        RelationshipKind kind,
        RelationshipStorageModel storageModel,
        RelationshipOrigin origin = RelationshipOrigin.Explicit)
        => new()
        {
            SourceType = sourceType,
            SourceTableName = sourceTableName,
            TargetType = targetType,
            TargetTableName = targetTableName,
            ClrMemberName = clrMemberName,
            StorageFieldName = storageFieldName,
            Kind = kind,
            StorageModel = storageModel,
            StorageKind = storageModel is RelationshipStorageModel.ScalarForeignKey
                ? RelationshipStorageKind.ScalarForeignKey
                : RelationshipStorageKind.RecordLink,
            Cardinality = kind is RelationshipKind.HasMany
                ? RelationshipCardinality.Many
                : RelationshipCardinality.One,
            Origin = origin
        };

    internal void SetFieldName(string fieldName) => StorageFieldName = fieldName;

    internal void SetRequired()
    {
        Required = true;
        Nullable = false;
    }

    internal void SetOptional()
    {
        Required = false;
        Nullable = true;
    }

    internal void SetReference(RelationshipOnDeleteAction? action = null, string? thenSurql = null)
    {
        Reference = true;
        OnDelete = action;
        OnDeleteThenSurql = thenSurql;
    }

    internal void SetUnique() => Unique = true;

    internal RelationshipDescriptor ToDescriptor(string targetIdMemberName, string targetIdFieldName)
        => new(
            SourceType,
            TargetType,
            SourceTableName,
            TargetTableName,
            ClrMemberName ?? StorageFieldName,
            StorageFieldName,
            targetIdMemberName,
            targetIdFieldName,
            StorageKind,
            Cardinality,
            Nullable,
            Required,
            Origin,
            WasOverridden,
            new RelationshipConstraints(Reference, OnDelete, OnDeleteThenSurql, Unique));
}

public enum RelationshipOnDeleteAction
{
    Ignore,
    Unset,
    Cascade,
    Then
}

public sealed class RelationshipBuilder<TSource>
{
    private readonly RelationshipMapping _mapping;

    internal RelationshipBuilder(RelationshipMapping mapping)
    {
        _mapping = mapping;
    }

    public RelationshipBuilder<TSource> Required()
    {
        _mapping.SetRequired();
        return this;
    }

    public RelationshipBuilder<TSource> Optional()
    {
        _mapping.SetOptional();
        return this;
    }

    public RelationshipBuilder<TSource> FieldName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Field name cannot be empty.", nameof(name));

        _mapping.SetFieldName(name);
        return this;
    }

    public RelationshipBuilder<TSource> Reference()
    {
        _mapping.SetReference();
        return this;
    }

    public RelationshipBuilder<TSource> OnDeleteIgnore()
    {
        _mapping.SetReference(RelationshipOnDeleteAction.Ignore);
        return this;
    }

    public RelationshipBuilder<TSource> OnDeleteUnset()
    {
        _mapping.SetReference(RelationshipOnDeleteAction.Unset);
        return this;
    }

    public RelationshipBuilder<TSource> OnDeleteCascade()
    {
        _mapping.SetReference(RelationshipOnDeleteAction.Cascade);
        return this;
    }

    public RelationshipBuilder<TSource> OnDeleteThen(string surqlBlock)
    {
        if (string.IsNullOrWhiteSpace(surqlBlock))
            throw new ArgumentException("SurrealQL block cannot be empty.", nameof(surqlBlock));

        _mapping.SetReference(RelationshipOnDeleteAction.Then, surqlBlock);
        return this;
    }

    public RelationshipBuilder<TSource> Unique()
    {
        _mapping.SetUnique();
        return this;
    }
}
