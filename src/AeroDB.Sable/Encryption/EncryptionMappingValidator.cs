using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

internal static class EncryptionMappingValidator
{
    internal static void Validate(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var mapping in options.Schema.Mappings.Values)
            ValidateMapping(options, mapping);
    }

    private static void ValidateMapping(StoreOptions options, DocumentMapping mapping)
    {
        var documentType = mapping.EntityType;
        var encryptedFields = EncryptedFieldResolver.GetFields(documentType, options.Schema);
        var blindIndexes = BlindIndexResolver.GetFields(documentType, options.Schema);
        if (encryptedFields.Count == 0 && blindIndexes.Count == 0)
            return;

        if (options.ClientFactory is not null
            && !options.Encryption.ExternalClientDisablesProtectedDataLogging)
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                "ClientFactory supplies an external SurrealDB client. Set " +
                "StoreOptions.Encryption.ExternalClientDisablesProtectedDataLogging only after " +
                "disabling that client's query-value and CBOR serialization logging.");
        }

        if (encryptedFields.Count > 0 && options.Encryption.Provider is null)
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                "StoreOptions.Encryption.Provider is required.");
        }

        var metadata = MetadataRegistry.TryGet(documentType);
        var identityProperty = mapping.IdentityProperty
            ?? (metadata?.GetIdentityAccessor is not null ? "Id" : null);
        if (identityProperty is null)
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                "a stable application-assigned identity must be configured before encrypted fields.");
        }

        foreach (var field in encryptedFields)
        {
            if (string.Equals(field.PropertyName, identityProperty, StringComparison.OrdinalIgnoreCase))
                ThrowConflict(documentType, field.PropertyName, "document identity");

            ValidateFieldShape(documentType, field);

            if ((mapping.IsMultiTenanted || metadata?.HasTenantId == true)
                && string.Equals(field.PropertyName, "TenantId", StringComparison.OrdinalIgnoreCase))
            {
                ThrowConflict(documentType, field.PropertyName, "tenant routing");
            }

            if (metadata?.VersionFieldName is { } versionField
                && string.Equals(field.PropertyName, versionField, StringComparison.OrdinalIgnoreCase))
            {
                ThrowConflict(documentType, field.PropertyName, "optimistic concurrency");
            }

            var storageField = mapping.ResolveFieldName(field.PropertyName);
            if (mapping.Indices.Any(index => index.Columns.Any(column =>
                    string.Equals(column, field.PropertyName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(column, storageField, StringComparison.OrdinalIgnoreCase))))
            {
                ThrowConflict(documentType, field.PropertyName, "database index");
            }

            if (mapping.GetRelationshipMappings().Any(relationship =>
                    string.Equals(
                        relationship.ClrMemberName,
                        field.PropertyName,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        relationship.StorageFieldName,
                        storageField,
                        StringComparison.OrdinalIgnoreCase)))
            {
                ThrowConflict(documentType, field.PropertyName, "relationship link");
            }
        }

        if (blindIndexes.Count > 0 && options.Encryption.BlindIndexProvider is null)
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                "StoreOptions.Encryption.BlindIndexProvider is required for blind indexes.");
        }

        var storageFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var blindIndex in blindIndexes)
        {
            if (blindIndex.Algorithm != BlindIndexAlgorithm.HmacSha256)
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind index on field '{blindIndex.PropertyName}' selects unsupported algorithm '{blindIndex.Algorithm}'.");
            }

            if (blindIndex.Normalizer != BlindIndexNormalizer.UsSocialSecurityNumberV1)
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind index on field '{blindIndex.PropertyName}' selects unsupported normalizer '{blindIndex.Normalizer}'.");
            }

            var encrypted = encryptedFields.FirstOrDefault(field =>
                string.Equals(field.PropertyName, blindIndex.PropertyName, StringComparison.Ordinal));
            if (encrypted is null)
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind-index field '{blindIndex.PropertyName}' must also be mapped with [Encrypt] or EncryptField(...).");
            }

            if (encrypted.ClrType != typeof(string))
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind-index field '{blindIndex.PropertyName}' must be a string.");
            }

            var storageField = BlindIndexResolver.ResolveStorageField(
                documentType,
                blindIndex,
                options.Schema);
            if (!IsSafeIdentifier(storageField))
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind-index storage field '{storageField}' is not a safe SurrealDB identifier.");
            }

            if (!storageFields.Add(storageField))
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind-index storage field '{storageField}' is configured more than once.");
            }

            var collidingProperty = documentType
                .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .FirstOrDefault(property =>
                    string.Equals(
                        mapping.ResolveFieldName(property.Name),
                        storageField,
                        StringComparison.OrdinalIgnoreCase));
            if (collidingProperty is not null)
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind-index storage field '{storageField}' collides with property '{collidingProperty.Name}'.");
            }

            if (mapping.GetFieldDefinitions().Any(field =>
                    string.Equals(
                        field.FieldName,
                        storageField,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind-index storage field '{storageField}' collides with a custom field definition.");
            }

            if (mapping.GetRelationshipMappings().Any(relationship =>
                    string.Equals(
                        relationship.StorageFieldName,
                        storageField,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind-index storage field '{storageField}' collides with a relationship field.");
            }

            if (string.Equals(storageField, "id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    storageField,
                    "last_modified_by",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SableEncryptionConfigurationException(
                    documentType,
                    $"blind-index storage field '{storageField}' is reserved by Sable.");
            }
        }

        if (mapping.ChangeTrackingConfig is not null)
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                "change tracking is not supported for encrypted documents in Phase B.");
        }
    }

    private static void ValidateFieldShape(Type documentType, EncryptedFieldDescriptor field)
    {
        if (field.ClrType != typeof(string) && field.ClrType != typeof(byte[]))
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                $"field '{field.PropertyName}' has unsupported CLR type '{field.ClrType.FullName}'.");
        }

        if (field.Algorithm is not (
            EncryptionAlgorithm.Aes256Gcm
            or EncryptionAlgorithm.ChaCha20Poly1305))
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                $"field '{field.PropertyName}' selects unsupported algorithm '{field.Algorithm}'.");
        }

        var supported = field.Algorithm switch
        {
            EncryptionAlgorithm.Aes256Gcm => System.Security.Cryptography.AesGcm.IsSupported,
            EncryptionAlgorithm.ChaCha20Poly1305 =>
                System.Security.Cryptography.ChaCha20Poly1305.IsSupported,
            _ => false
        };
        if (!supported)
        {
            throw new SableEncryptionConfigurationException(
                documentType,
                $"field '{field.PropertyName}' selects algorithm '{field.Algorithm}', " +
                "which is not supported on this platform.");
        }
    }

    private static void ThrowConflict(Type documentType, string propertyName, string role)
        => throw new SableEncryptionConfigurationException(
            documentType,
            $"field '{propertyName}' cannot also be used for {role}.");

    private static bool IsSafeIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
            return false;

        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(value[index]) && value[index] != '_')
                return false;
        }

        return true;
    }
}
