using System.Reflection;

namespace AeroDB.Sable.Metadata;

/// <summary>
/// Fast type-based dispatch for metadata lookups.
/// Delegates to <see cref="MetadataRegistry"/> for types that have generated metadata,
/// and falls back to runtime computation for all other types.
/// </summary>
internal static class MetadataDispatch
{
    /// <summary>
    /// Returns the snake_case table name for the given type.
    /// Uses generated metadata if available, otherwise computes at runtime.
    /// </summary>
    public static string GetTableName(Type type)
    {
        var meta = MetadataRegistry.TryGet(type);
        if (meta is not null)
            return meta.TableName;

        return ToSnakeCase(type.Name);
    }

    public static string GetTableName(Type type, SchemaOptions? schema)
    {
        if (schema?.Mappings.TryGetValue(type, out var mapping) == true)
            return mapping.TableNameOverride ?? GetTableName(type);

        return schema is not null
            ? schema.NamingPolicy.TableName(type)
            : GetTableName(type);
    }

    public static string GetFieldName(Type sourceType, string clrName, SchemaOptions? schema)
    {
        if (schema?.Mappings.TryGetValue(sourceType, out var configured) == true)
            return configured.ResolveFieldName(clrName);

        return schema is not null
            ? schema.NamingPolicy.FieldName(clrName)
            : ToSnakeCase(clrName);
    }

    /// <summary>
    /// Returns true if the type has a TenantId string property.
    /// Uses generated metadata if available, otherwise inspects via reflection.
    /// </summary>
    public static bool HasTenantId(Type type)
    {
        var meta = MetadataRegistry.TryGet(type);
        if (meta is not null)
            return meta.HasTenantId;

        return type.GetProperty("TenantId", typeof(string)) is { CanRead: true, CanWrite: true };
    }

    /// <summary>
    /// Returns the version field name for the type, or null.
    /// Uses generated metadata (which provides <see cref="ITypeMetadata.VersionFieldName"/>)
    /// when available, otherwise inspects via reflection.
    /// </summary>
    public static string? GetVersionFieldName(Type type)
    {
        // Prefer generated metadata — no reflection needed
        if (MetadataRegistry.TryGet(type) is ITypeMetadata meta && meta.HasVersion)
            return meta.VersionFieldName;

        // [Version] attribute takes precedence over IVersioned
        var prop = type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .FirstOrDefault(p => p.GetCustomAttribute<VersionAttribute>() is not null);
        if (prop is not null)
            return prop.Name;

        if (typeof(IVersioned).IsAssignableFrom(type))
            return "Version";

        return null;
    }

    /// <summary>
    /// Returns the schema target (database name and table name) for the given type.
    /// The database is the mapped <c>DocumentMapping.SchemaName</c> if configured,
    /// or null to indicate the default database (<c>StoreOptions.Database</c>).
    /// </summary>
    /// <param name="type">The entity type.</param>
    /// <param name="schema">The schema options containing document mappings.</param>
    public static (string? Database, string Table) GetSchemaTarget(Type type, SchemaOptions schema)
    {
        var table = GetTableName(type, schema);
        if (schema.Mappings.TryGetValue(type, out var mapping) && mapping.SchemaName is not null)
            return (mapping.SchemaName, table);
        return (null, table);
    }

    /// <summary>
    /// Pre-computed snake_case conversion (shared with generated metadata).
    /// </summary>
    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
