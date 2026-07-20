using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

internal static class EncryptedFieldResolver
{
    internal static IReadOnlyList<EncryptedFieldDescriptor> GetFields(
        Type documentType,
        SchemaOptions schema)
    {
        var generated = MetadataRegistry.TryGet(documentType)?.EncryptedFields ?? [];
        if (generated.Count > 0 && !schema.Mappings.ContainsKey(documentType))
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                "attribute-declared encryption requires explicit Schema.For<T>() registration " +
                "so startup validation and schema generation cannot be bypassed.");
        }

        var fluent = schema.Mappings.TryGetValue(documentType, out var mapping)
            ? mapping.GetEncryptedFields()
            : [];

        if (generated.Count == 0)
            return fluent;
        if (fluent.Count == 0)
            return generated;

        var merged = generated.ToDictionary(field => field.PropertyName, StringComparer.Ordinal);
        foreach (var field in fluent)
            merged[field.PropertyName] = field;
        return merged.Values.ToArray();
    }

    internal static bool HasEncryptedFields(Type documentType, SchemaOptions schema)
        => GetFields(documentType, schema).Count > 0;

    internal static EncryptedFieldDescriptor? Find(
        Type documentType,
        string propertyName,
        SchemaOptions schema)
        => GetFields(documentType, schema)
            .FirstOrDefault(field => string.Equals(field.PropertyName, propertyName, StringComparison.Ordinal));
}
