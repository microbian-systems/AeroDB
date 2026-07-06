namespace AeroDB;

public enum RelationshipKind
{
    HasOne,
    HasMany
}

public enum RelationshipStorageModel
{
    RecordLink,
    RecordLinkArray,
    GraphEdge
}

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
        RelationshipStorageModel storageModel)
        => new()
        {
            SourceType = sourceType,
            SourceTableName = sourceTableName,
            TargetType = targetType,
            TargetTableName = targetTableName,
            ClrMemberName = clrMemberName,
            StorageFieldName = storageFieldName,
            Kind = kind,
            StorageModel = storageModel
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
