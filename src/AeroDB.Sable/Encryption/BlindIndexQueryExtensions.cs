using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

/// <summary>Explicit equality lookup over separately keyed blind indexes.</summary>
public static class BlindIndexQueryExtensions
{
    /// <summary>
    /// Finds encrypted documents by a mapped blind index. Only versioned tokens
    /// are sent to SurrealDB; every hit is decrypted and compared in fixed time.
    /// </summary>
    public static async Task<List<T>> WhereEncryptedEqualsAsync<T>(
        this IQuerySession session,
        Expression<Func<T, string?>> property,
        string value,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(value);

        if (session is not InternalSessionBase internalSession)
        {
            throw new SableBlindIndexConfigurationException(
                "Blind-index lookup requires a Sable-owned query or document session " +
                "with the protected materialization pipeline.");
        }

        var options = internalSession.StoreOptions;
        var propertyInfo = ExtractProperty(property);
        var descriptor = BlindIndexResolver.Find(
                typeof(T),
                propertyInfo.Name,
                options.Schema)
            ?? throw new SableBlindIndexConfigurationException(
                $"Field '{typeof(T).FullName}.{propertyInfo.Name}' does not have a blind-index mapping.");
        var encrypted = EncryptedFieldResolver.Find(
            typeof(T),
            propertyInfo.Name,
            options.Schema);
        if (encrypted is null)
        {
            throw new SableBlindIndexConfigurationException(
                $"Blind-index field '{typeof(T).FullName}.{propertyInfo.Name}' must also be encrypted.");
        }

        var provider = options.Encryption.BlindIndexProvider
            ?? throw new SableBlindIndexConfigurationException(
                "StoreOptions.Encryption.BlindIndexProvider is required for blind-index lookup.");
        var storageField = BlindIndexResolver.ResolveStorageField(
            typeof(T),
            descriptor,
            options.Schema);
        var (mappedDatabase, table) = MetadataDispatch.GetSchemaTarget(
            typeof(T),
            options.Schema);
        var context = new BlindIndexContext(
            options.Namespace,
            mappedDatabase ?? options.Database,
            table,
            session.TenantId,
            storageField,
            descriptor.Normalizer);
        var normalizedCandidate = BlindIndexNormalization.Normalize(value, descriptor.Normalizer);

        try
        {
            var tokens = await provider
                .CreateQueryTokensAsync(
                    normalizedCandidate,
                    context,
                    descriptor.Algorithm,
                    cancellationToken)
                .ConfigureAwait(false);
            if (tokens.Count == 0)
            {
                throw new SableBlindIndexConfigurationException(
                    "The blind-index provider returned no searchable key versions.");
            }

            var parameters = new Dictionary<string, object?>(tokens.Count, StringComparer.Ordinal);
            var predicates = new string[tokens.Count];
            for (var index = 0; index < tokens.Count; index++)
            {
                var parameterName = $"blind_index_{index}";
                parameters[parameterName] = tokens[index];
                predicates[index] = $"{storageField} = ${parameterName}";
            }

            var filters = new List<string>
            {
                "(" + string.Join(" OR ", predicates) + ")"
            };
            ApplyTenantFilter<T>(session, options, parameters, filters);
            if (options.SoftDeleteEnabled && typeof(ISoftDeleted).IsAssignableFrom(typeof(T)))
            {
                var deletedField = MetadataDispatch.GetFieldName(
                    typeof(T),
                    nameof(ISoftDeleted.Deleted),
                    options.Schema);
                filters.Add($"{deletedField} = false");
            }

            var sql = $"SELECT * FROM {table} WHERE {string.Join(" AND ", filters)};";
            var results = await internalSession
                .RawDocumentQueryAsync<T>(sql, parameters, cancellationToken)
                .ConfigureAwait(false);
            VerifyResults(results, descriptor, normalizedCandidate);
            return results;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalizedCandidate);
        }
    }

    internal static void VerifyResults<T>(
        IEnumerable<T> results,
        BlindIndexDescriptor descriptor,
        ReadOnlySpan<byte> normalizedCandidate)
    {
        foreach (var result in results)
        {
            var decryptedValue = descriptor.GetValue(result!);
            if (decryptedValue is null
                || !MatchesNormalized(
                    decryptedValue,
                    normalizedCandidate,
                    descriptor.Normalizer))
            {
                throw new SableBlindIndexIntegrityException(typeof(T), descriptor.PropertyName);
            }
        }
    }

    private static void ApplyTenantFilter<T>(
        IQuerySession session,
        StoreOptions options,
        IDictionary<string, object?> parameters,
        ICollection<string> filters)
    {
        if (options.TenancyStyle == TenancyStyle.DatabasePerTenant)
            return;

        var mapping = options.Schema.Mappings.GetValueOrDefault(typeof(T));
        var hasTenantField = MetadataRegistry.TryGet(typeof(T))?.HasTenantId == true
            || typeof(T).GetProperty(
                "TenantId",
                BindingFlags.Instance | BindingFlags.Public) is
                { PropertyType: var propertyType } && propertyType == typeof(string);
        if (mapping?.IsMultiTenanted == true && string.IsNullOrWhiteSpace(session.TenantId))
        {
            throw new SableBlindIndexConfigurationException(
                $"Blind-index lookup for multi-tenanted document type '{typeof(T).FullName}' " +
                "requires a tenant-scoped session.");
        }

        if (!hasTenantField || string.IsNullOrWhiteSpace(session.TenantId))
            return;

        var tenantField = MetadataDispatch.GetFieldName(
            typeof(T),
            "TenantId",
            options.Schema);
        parameters["blind_index_tenant"] = session.TenantId;
        filters.Add($"{tenantField} = $blind_index_tenant");
    }

    private static bool MatchesNormalized(
        string decryptedValue,
        ReadOnlySpan<byte> normalizedCandidate,
        BlindIndexNormalizer normalizer)
    {
        byte[] normalizedResult;
        try
        {
            normalizedResult = BlindIndexNormalization.Normalize(decryptedValue, normalizer);
        }
        catch (SableBlindIndexNormalizationException)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                normalizedResult,
                normalizedCandidate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalizedResult);
        }
    }

    private static PropertyInfo ExtractProperty<T>(
        Expression<Func<T, string?>> property)
    {
        var body = property.Body;
        while (body is UnaryExpression
               {
                   NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
               } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression { Member: PropertyInfo propertyInfo }
            || body is MemberExpression { Expression: not ParameterExpression })
        {
            throw new ArgumentException(
                "Blind-index lookup requires a direct property selector.",
                nameof(property));
        }

        return propertyInfo;
    }
}
