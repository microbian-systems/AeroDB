using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

internal static class BlindIndexResolver
{
    internal static IReadOnlyList<BlindIndexDescriptor> GetFields(
        Type documentType,
        SchemaOptions schema)
    {
        var generated = MetadataRegistry.TryGet(documentType)?.BlindIndexes ?? [];
        if (generated.Count > 0 && !schema.Mappings.ContainsKey(documentType))
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                "attribute-declared blind indexes require explicit Schema.For<T>() registration " +
                "so startup validation and schema generation cannot be bypassed.");
        }

        var fluent = schema.Mappings.TryGetValue(documentType, out var mapping)
            ? mapping.GetBlindIndexes()
            : [];

        if (generated.Count == 0)
            return fluent;
        if (fluent.Count == 0)
            return generated;

        var merged = generated.ToDictionary(field => field.PropertyName, StringComparer.Ordinal);
        foreach (var field in fluent)
        {
            if (merged.TryGetValue(field.PropertyName, out var attributed)
                && (attributed.Algorithm != field.Algorithm
                    || attributed.Normalizer != field.Normalizer
                    || !string.Equals(
                        ResolveStorageField(documentType, attributed, schema),
                        ResolveStorageField(documentType, field, schema),
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new SableBlindIndexConfigurationException(
                    $"Attribute and fluent blind-index mappings conflict for " +
                    $"'{documentType.FullName}.{field.PropertyName}'.");
            }

            merged[field.PropertyName] = field;
        }
        return merged.Values.ToArray();
    }

    internal static BlindIndexDescriptor? Find(
        Type documentType,
        string propertyName,
        SchemaOptions schema)
        => GetFields(documentType, schema)
            .FirstOrDefault(field =>
                string.Equals(field.PropertyName, propertyName, StringComparison.Ordinal));

    internal static string ResolveStorageField(
        Type documentType,
        BlindIndexDescriptor descriptor,
        SchemaOptions schema)
    {
        if (!string.IsNullOrWhiteSpace(descriptor.StorageFieldName))
            return descriptor.StorageFieldName!;

        return Metadata.MetadataDispatch.GetFieldName(
            documentType,
            descriptor.PropertyName,
            schema) + "_bidx";
    }
}
