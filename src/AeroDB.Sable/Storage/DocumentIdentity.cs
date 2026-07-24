using System.Globalization;
using AeroDB.Sable.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB.Sable;

/// <summary>
/// Resolves a CLR document identity without erasing its type and converts it to
/// SurrealDB's native record-id representation.
/// </summary>
internal readonly record struct DocumentIdentity(
    object Value,
    RecordId RecordId,
    string Key)
{
    public string Literal => DocumentIdentityResolver.FormatRecordIdLiteral(RecordId);
}

internal static class DocumentIdentityResolver
{
    public static object NormalizeForDocumentType(
        Type documentType,
        object identity,
        SchemaOptions schema)
    {
        ArgumentNullException.ThrowIfNull(documentType);
        ArgumentNullException.ThrowIfNull(identity);

        if (identity is not string stringIdentity)
            return identity;

        var mapping = schema.Mappings.GetValueOrDefault(documentType);
        var identityPropertyName = mapping?.IdentityProperty ?? "Id";
        var identityType = !string.IsNullOrWhiteSpace(mapping?.IdentityProperty)
            ? documentType.GetProperty(identityPropertyName)?.PropertyType
            : MetadataRegistry.TryGet(documentType)?.IdentityType
                ?? documentType.GetProperty(identityPropertyName)?.PropertyType;

        if (identityType is null)
            return identity;

        return NormalizeForIdentityType(identityType, identity);
    }

    public static object NormalizeForIdentityType(Type identityType, object identity)
    {
        ArgumentNullException.ThrowIfNull(identityType);
        ArgumentNullException.ThrowIfNull(identity);

        if (identity is not string stringIdentity)
            return identity;

        identityType = Nullable.GetUnderlyingType(identityType) ?? identityType;
        if (identityType == typeof(string)
            || identityType == typeof(object)
            || identityType == typeof(RecordId))
        {
            return identity;
        }

        if (identityType.IsGenericType
            && identityType.GetGenericTypeDefinition() == typeof(RecordIdOf<>))
        {
            identityType = identityType.GetGenericArguments()[0];
        }

        return identityType switch
        {
            _ when identityType == typeof(long) => long.Parse(stringIdentity, CultureInfo.InvariantCulture),
            _ when identityType == typeof(int) => int.Parse(stringIdentity, CultureInfo.InvariantCulture),
            _ when identityType == typeof(short) => short.Parse(stringIdentity, CultureInfo.InvariantCulture),
            _ when identityType == typeof(byte) => byte.Parse(stringIdentity, CultureInfo.InvariantCulture),
            _ when identityType == typeof(ulong) => ulong.Parse(stringIdentity, CultureInfo.InvariantCulture),
            _ when identityType == typeof(uint) => uint.Parse(stringIdentity, CultureInfo.InvariantCulture),
            _ when identityType == typeof(ushort) => ushort.Parse(stringIdentity, CultureInfo.InvariantCulture),
            _ when identityType == typeof(sbyte) => sbyte.Parse(stringIdentity, CultureInfo.InvariantCulture),
            _ when identityType == typeof(Guid) => Guid.Parse(stringIdentity),
            _ => identity
        };
    }

    public static bool TryResolve(
        object entity,
        string table,
        SchemaOptions schema,
        out DocumentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity is IRecord { Id: not null } record)
            return TryCreate(record.Id, table, out identity);

        var entityType = entity.GetType();
        var mapping = schema.Mappings.GetValueOrDefault(entityType);
        object? value;

        if (!string.IsNullOrWhiteSpace(mapping?.IdentityProperty))
        {
            value = entityType.GetProperty(mapping.IdentityProperty)?.GetValue(entity);
        }
        else
        {
            var metadata = MetadataRegistry.TryGet(entityType);
            value = metadata?.GetIdentityAccessor?.Invoke(entity)
                ?? entityType.GetProperty("Id")?.GetValue(entity);
        }

        return TryCreate(value, table, out identity);
    }

    public static bool TryCreate(object? value, string table, out DocumentIdentity identity)
    {
        if (value is null)
        {
            identity = default;
            return false;
        }

        if (value is string { Length: 0 })
        {
            identity = default;
            return false;
        }

        var recordId = value switch
        {
            RecordId existing => existing,
            string id => RecordId.From(table, id),
            long id => RecordId.From(table, id),
            int id => RecordId.From(table, id),
            short id => RecordId.From(table, id),
            byte id => RecordId.From(table, id),
            ulong id => new RecordIdOf<ulong>(table, id),
            uint id => new RecordIdOf<uint>(table, id),
            ushort id => new RecordIdOf<ushort>(table, id),
            sbyte id => new RecordIdOf<sbyte>(table, id),
            Guid id => RecordId.From(table, id),
            _ => RecordId.From(table, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };

        var rawValue = value is RecordId
            ? GetRecordIdValue(recordId)
            : value;
        var key = ToIdentityKey(rawValue);
        if (key.Length == 0)
        {
            identity = default;
            return false;
        }

        identity = new DocumentIdentity(rawValue, recordId, key);
        return true;
    }

    public static string FormatRecordIdLiteral(RecordId recordId)
    {
        ArgumentNullException.ThrowIfNull(recordId);
        return $"{recordId.Table}:{FormatIdentityLiteral(GetRecordIdValue(recordId))}";
    }

    public static string FormatRecordIdLiteral(string table, object identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(identity);
        return $"{table}:{FormatIdentityLiteral(identity)}";
    }

    public static string ToIdentityKey(object identity)
        => identity switch
        {
            string value => value,
            Guid value => value.ToString(),
            DateTime value => value.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset value => value.ToString("O", CultureInfo.InvariantCulture),
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => identity.ToString() ?? string.Empty
        };

    private static object GetRecordIdValue(RecordId recordId)
    {
        var type = recordId.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RecordIdOf<>))
            return type.GetProperty(nameof(RecordIdOf<object>.Id))!.GetValue(recordId)!;

        return recordId.DeserializeId<object>();
    }

    private static string FormatIdentityLiteral(object identity)
        => identity switch
        {
            sbyte value => value.ToString(CultureInfo.InvariantCulture),
            byte value => value.ToString(CultureInfo.InvariantCulture),
            short value => value.ToString(CultureInfo.InvariantCulture),
            ushort value => value.ToString(CultureInfo.InvariantCulture),
            int value => value.ToString(CultureInfo.InvariantCulture),
            uint value => value.ToString(CultureInfo.InvariantCulture),
            long value => value.ToString(CultureInfo.InvariantCulture),
            ulong value => value.ToString(CultureInfo.InvariantCulture),
            string value => Quote(value),
            Guid value => $"u'{value:D}'",
            DateTime value => Quote(value.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset value => Quote(value.ToString("O", CultureInfo.InvariantCulture)),
            _ => Quote(Convert.ToString(identity, CultureInfo.InvariantCulture) ?? string.Empty)
        };

    private static string Quote(string value)
        => "`" + value.Replace("`", "\\`", StringComparison.Ordinal) + "`";
}
