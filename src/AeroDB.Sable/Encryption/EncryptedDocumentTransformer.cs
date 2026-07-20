using System.Text;
using AeroDB.Sable.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB.Sable;

internal static class EncryptedDocumentTransformer
{
    internal static async ValueTask<ProtectedDocumentValues> ProtectFieldsAsync(
        object entity,
        string recordId,
        StoreOptions options,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var entityType = entity.GetType();
        var fields = EncryptedFieldResolver.GetFields(entityType, options.Schema);
        var blindIndexes = BlindIndexResolver.GetFields(entityType, options.Schema);
        if (fields.Count == 0 && blindIndexes.Count == 0)
            return ProtectedDocumentValues.Empty;
        if (string.IsNullOrWhiteSpace(recordId))
            throw new SableEncryptedDocumentRequiresIdException(entityType);

        var provider = fields.Count > 0
            ? options.Encryption.Provider
                ?? throw new SableEncryptionException(
                    $"Encrypted document type '{entityType.FullName}' requires StoreOptions.Encryption.Provider.")
            : null;
        var (mappedDatabase, table) = MetadataDispatch.GetSchemaTarget(entityType, options.Schema);
        var database = mappedDatabase ?? options.Database;
        tenantId = ResolveWriteTenant(entity, entityType, options, tenantId);
        var overrides = new Dictionary<string, object?>(StringComparer.Ordinal);
        var additionalFields = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            var storageField = MetadataDispatch.GetFieldName(entityType, field.PropertyName, options.Schema);
            var value = field.GetValue(entity);
            if (value is null)
            {
                overrides[field.PropertyName] = null;
                continue;
            }

            var plaintext = Encode(value, field);
            try
            {
                var context = new EncryptionContext(
                    options.Namespace,
                    database,
                    table,
                    recordId,
                    tenantId,
                    storageField,
                    field.CodecId);
                var envelope = await provider!
                    .ProtectAsync(plaintext, context, field.Algorithm, cancellationToken)
                    .ConfigureAwait(false);
                overrides[field.PropertyName] = envelope.ToStorageObject();
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        if (blindIndexes.Count > 0)
        {
            var blindIndexProvider = options.Encryption.BlindIndexProvider
                ?? throw new SableBlindIndexConfigurationException(
                    $"Blind-indexed document type '{entityType.FullName}' requires " +
                    "StoreOptions.Encryption.BlindIndexProvider.");

            foreach (var blindIndex in blindIndexes)
            {
                var storageField = BlindIndexResolver.ResolveStorageField(
                    entityType,
                    blindIndex,
                    options.Schema);
                var value = blindIndex.GetValue(entity);
                if (value is null)
                {
                    additionalFields[storageField] = null;
                    continue;
                }

                var normalized = BlindIndexNormalization.Normalize(value, blindIndex.Normalizer);
                try
                {
                    var context = new BlindIndexContext(
                        options.Namespace,
                        database,
                        table,
                        tenantId,
                        storageField,
                        blindIndex.Normalizer);
                    additionalFields[storageField] = await blindIndexProvider
                        .CreateStorageTokenAsync(
                            normalized,
                            context,
                            blindIndex.Algorithm,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(normalized);
                }
            }
        }

        return new ProtectedDocumentValues(overrides, additionalFields);
    }

    internal static async ValueTask DecryptRecordsAsync<T>(
        List<Dictionary<string, object?>> records,
        StoreOptions options,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var entityType = typeof(T);
        var fields = EncryptedFieldResolver.GetFields(entityType, options.Schema);
        if (fields.Count == 0 || records.Count == 0)
            return;

        var provider = options.Encryption.Provider
            ?? throw new SableEncryptionException(
                $"Encrypted document type '{entityType.FullName}' requires StoreOptions.Encryption.Provider.");
        var (mappedDatabase, table) = MetadataDispatch.GetSchemaTarget(entityType, options.Schema);
        var database = mappedDatabase ?? options.Database;

        foreach (var record in records)
        {
            var recordId = ExtractRecordId(record, table)
                ?? throw new SableEnvelopeException(
                    $"Encrypted '{entityType.FullName}' record does not contain a stable record ID.");

            var recordTenantId = ResolveReadTenant(
                record,
                entityType,
                options,
                tenantId);

            foreach (var field in fields)
            {
                var storageField = MetadataDispatch.GetFieldName(entityType, field.PropertyName, options.Schema);
                if (!TryGetCaseInsensitive(record, storageField, out var storageValue))
                {
                    throw new SableEnvelopeException(
                        $"Encrypted field '{entityType.FullName}.{field.PropertyName}' is missing " +
                        "from a typed document result.");
                }
                if (storageValue is null)
                    continue;

                var envelope = EncryptedEnvelope.Parse(
                    storageValue,
                    options.Encryption.MaximumEnvelopeCiphertextBytes);
                var context = new EncryptionContext(
                    options.Namespace,
                    database,
                    table,
                    recordId,
                    recordTenantId,
                    storageField,
                    field.CodecId);

                using var plaintext = await provider
                    .UnprotectAsync(envelope, context, cancellationToken)
                    .ConfigureAwait(false);
                record[storageField] = Decode(plaintext.Memory.Span, field);
            }
        }
    }

    private static byte[] Encode(object value, EncryptedFieldDescriptor field)
        => field.CodecId switch
        {
            "utf8-string-v1" when value is string text => Encoding.UTF8.GetBytes(text),
            "bytes-v1" when value is byte[] bytes => bytes.ToArray(),
            _ => throw new SableEncryptionException(
                $"Encrypted field '{field.PropertyName}' value does not match codec '{field.CodecId}'.")
        };

    private static object Decode(ReadOnlySpan<byte> plaintext, EncryptedFieldDescriptor field)
        => field.CodecId switch
        {
            "utf8-string-v1" => Encoding.UTF8.GetString(plaintext),
            "bytes-v1" => plaintext.ToArray(),
            _ => throw new SableEnvelopeException(
                $"Encrypted field '{field.PropertyName}' uses unsupported codec '{field.CodecId}'.")
        };

    private static string? ExtractRecordId(Dictionary<string, object?> record, string table)
    {
        if (!TryGetCaseInsensitive(record, "id", out var value) || value is null)
            return null;

        var text = value switch
        {
            RecordIdOf<string> stringId => stringId.Id,
            RecordIdOf<long> longId => longId.Id.ToString(),
            RecordIdOf<int> intId => intId.Id.ToString(),
            RecordId recordId => TryDeserializeRecordId(recordId),
            _ => value.ToString()
        };
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var prefix = table + ":";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? text[prefix.Length..].Trim('`', '⟨', '⟩')
            : text.Trim('`', '⟨', '⟩');
    }

    private static string? TryDeserializeRecordId(RecordId recordId)
    {
        try { return recordId.DeserializeId<string>(); } catch { }
        try { return recordId.DeserializeId<long>().ToString(); } catch { }
        try { return recordId.DeserializeId<int>().ToString(); } catch { }
        return null;
    }

    private static bool TryGetCaseInsensitive(
        Dictionary<string, object?> record,
        string key,
        out object? value)
    {
        if (record.TryGetValue(key, out value))
            return true;

        var match = record.Keys.FirstOrDefault(candidate =>
            string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        value = record[match];
        return true;
    }

    private static string? ResolveWriteTenant(
        object entity,
        Type entityType,
        StoreOptions options,
        string? sessionTenantId)
    {
        if (!options.Schema.Mappings.TryGetValue(entityType, out var mapping)
            || !mapping.IsMultiTenanted
            || options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            return sessionTenantId;
        }

        if (string.IsNullOrWhiteSpace(sessionTenantId))
        {
            throw new SableEncryptionException(
                $"Encrypted multi-tenanted document type '{entityType.FullName}' " +
                "requires a tenant-scoped session.");
        }

        var entityTenantId = entityType.GetProperty("TenantId")?.GetValue(entity) as string;
        if (!string.Equals(entityTenantId, sessionTenantId, StringComparison.Ordinal))
        {
            throw new SableEncryptionException(
                $"Encrypted document tenant '{entityTenantId}' does not match " +
                $"session tenant '{sessionTenantId}'.");
        }

        return sessionTenantId;
    }

    private static string? ResolveReadTenant(
        Dictionary<string, object?> record,
        Type entityType,
        StoreOptions options,
        string? sessionTenantId)
    {
        if (!options.Schema.Mappings.TryGetValue(entityType, out var mapping)
            || !mapping.IsMultiTenanted
            || options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            return sessionTenantId;
        }

        if (string.IsNullOrWhiteSpace(sessionTenantId))
        {
            throw new SableEncryptionException(
                $"Encrypted multi-tenanted document type '{entityType.FullName}' " +
                "requires a tenant-scoped session.");
        }

        var tenantField = MetadataDispatch.GetFieldName(
            entityType,
            "TenantId",
            options.Schema);
        if (!TryGetCaseInsensitive(record, tenantField, out var storedTenant)
            || !string.Equals(
                storedTenant?.ToString(),
                sessionTenantId,
                StringComparison.Ordinal))
        {
            throw new SableEncryptionException(
                $"Encrypted document tenant does not match session tenant '{sessionTenantId}'.");
        }

        return sessionTenantId;
    }

}

internal sealed record ProtectedDocumentValues(
    IReadOnlyDictionary<string, object?> PropertyOverrides,
    IReadOnlyDictionary<string, object?> AdditionalStorageFields)
{
    internal static readonly ProtectedDocumentValues Empty =
        new(
            new Dictionary<string, object?>(StringComparer.Ordinal),
            new Dictionary<string, object?>(StringComparer.Ordinal));
}
