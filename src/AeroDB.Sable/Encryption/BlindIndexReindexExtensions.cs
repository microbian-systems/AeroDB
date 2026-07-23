using System.Security.Cryptography;
using AeroDB.Sable.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB.Sable;

/// <summary>Explicit blind-index maintenance operations for key rotation.</summary>
public static class BlindIndexReindexExtensions
{
    /// <summary>
    /// Decrypts the selected document set and rewrites every mapped blind-index
    /// sidecar with the provider's active key. The operation is idempotent.
    /// </summary>
    public static async Task<BlindIndexReindexResult> ReindexBlindIndexesAsync<T>(
        this IDocumentSession session,
        int batchSize = 100,
        RecordId? after = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);
        if (batchSize is <= 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (session is not DocumentSession documentSession)
        {
            throw new SableBlindIndexConfigurationException(
                "Blind-index reindexing requires a Sable-owned document session.");
        }

        var options = documentSession.StoreOptions;
        var descriptors = BlindIndexResolver.GetFields(typeof(T), options.Schema);
        if (descriptors.Count == 0)
        {
            throw new SableBlindIndexConfigurationException(
                $"Document type '{typeof(T).FullName}' does not have a blind-index mapping.");
        }

        var provider = options.Encryption.BlindIndexProvider
            ?? throw new SableBlindIndexConfigurationException(
                "StoreOptions.Encryption.BlindIndexProvider is required for reindexing.");
        var (mappedDatabase, table) = MetadataDispatch.GetSchemaTarget(
            typeof(T), options.Schema);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var filters = BuildFilters<T>(documentSession, options, parameters);
        if (after is not null)
        {
            if (!string.Equals(after.Table, table, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The reindex cursor targets table '{after.Table}', but '{table}' was expected.",
                    nameof(after));
            }

            parameters["reindex_after"] = after;
            filters += filters.Length == 0
                ? " WHERE id > $reindex_after"
                : " AND id > $reindex_after";
        }
        parameters["reindex_limit"] = batchSize + 1;
        var select = $"SELECT * FROM {table}{filters} ORDER BY id " +
            "LIMIT $reindex_limit;";
        var selected = await documentSession
            .RawDocumentQueryAsync<T>(select, parameters, cancellationToken)
            .ConfigureAwait(false);
        var hasMore = selected.Count > batchSize;
        var documents = selected.Take(batchSize).ToList();
        if (documents.Count == 0)
            return new BlindIndexReindexResult(0, 0, after, false);

        var updateParameters = new Dictionary<string, object?>(
            documents.Count * (descriptors.Count + 2),
            StringComparer.Ordinal);
        var statements = new List<string>(documents.Count);
        var tokensWritten = 0;
        RecordId? nextCursor = after;

        for (var documentIndex = 0; documentIndex < documents.Count; documentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = documents[documentIndex];
            var recordId = documentSession.GetRecordIdForProtectedOperation(document, table)
                ?? throw new SableEncryptedDocumentRequiresIdException(typeof(T));
            nextCursor = recordId;
            var assignments = new List<string>(descriptors.Count);
            var predicates = new List<string>(descriptors.Count);
            updateParameters[$"reindex_record_{documentIndex}"] =
                recordId;

            for (var fieldIndex = 0; fieldIndex < descriptors.Count; fieldIndex++)
            {
                var descriptor = descriptors[fieldIndex];
                var storageField = BlindIndexResolver.ResolveStorageField(
                    typeof(T), descriptor, options.Schema);
                var parameterName = $"reindex_token_{documentIndex}_{fieldIndex}";
                var value = descriptor.GetValue(document);
                if (value is null)
                {
                    updateParameters[parameterName] = null;
                    predicates.Add($"`{EscapeIdentifier(storageField)}` = NONE");
                }
                else
                {
                    var tokens = await CreateTokensAsync(
                        value,
                        provider,
                        new BlindIndexContext(
                            options.Namespace,
                            mappedDatabase ?? options.Database,
                            table,
                            documentSession.TenantId,
                            storageField,
                            descriptor.Normalizer),
                        descriptor,
                        cancellationToken).ConfigureAwait(false);
                    updateParameters[parameterName] = tokens.Active;
                    if (tokens.Search.Count == 0)
                    {
                        throw new SableBlindIndexConfigurationException(
                            "The blind-index provider returned no searchable key versions.");
                    }

                    var expected = new string[tokens.Search.Count];
                    for (var tokenIndex = 0; tokenIndex < tokens.Search.Count; tokenIndex++)
                    {
                        var expectedName =
                            $"reindex_expected_{documentIndex}_{fieldIndex}_{tokenIndex}";
                        updateParameters[expectedName] = tokens.Search[tokenIndex];
                        expected[tokenIndex] =
                            $"`{EscapeIdentifier(storageField)}` = ${expectedName}";
                    }
                    predicates.Add("(" + string.Join(" OR ", expected) + ")");
                }
                assignments.Add($"`{EscapeIdentifier(storageField)}` = ${parameterName}");
                tokensWritten++;
            }

            statements.Add(
                $"UPDATE $reindex_record_{documentIndex} " +
                $"SET {string.Join(", ", assignments)} " +
                $"WHERE {string.Join(" AND ", predicates)};");
        }

        var targetSession = await documentSession
            .GetSessionForSchemaAsync(mappedDatabase, cancellationToken)
            .ConfigureAwait(false);
        var response = await targetSession
            .RawQuery(string.Join(Environment.NewLine, statements), updateParameters, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureAllOks();
        for (var index = 0; index < response.Count; index++)
        {
            if ((response.GetValue<List<object>>(index)?.Count ?? 0) == 0)
                throw new SableBlindIndexReindexConflictException(typeof(T));
        }

        return new BlindIndexReindexResult(
            documents.Count,
            tokensWritten,
            nextCursor,
            hasMore);
    }

    private static async ValueTask<(string Active, IReadOnlyList<string> Search)> CreateTokensAsync(
        string value,
        IBlindIndexTokenProvider provider,
        BlindIndexContext context,
        BlindIndexDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var normalized = BlindIndexNormalization.Normalize(value, descriptor.Normalizer);
        try
        {
            var active = await provider.CreateStorageTokenAsync(
                    normalized, context, descriptor.Algorithm, cancellationToken)
                .ConfigureAwait(false);
            var search = await provider.CreateQueryTokensAsync(
                    normalized, context, descriptor.Algorithm, cancellationToken)
                .ConfigureAwait(false);
            return (active, search);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalized);
        }
    }

    private static string BuildFilters<T>(
        IQuerySession session,
        StoreOptions options,
        IDictionary<string, object?> parameters)
    {
        var filters = new List<string>();
        var mapping = options.Schema.Mappings.GetValueOrDefault(typeof(T));
        var hasTenantField = MetadataRegistry.TryGet(typeof(T))?.HasTenantId == true
            || typeof(T).GetProperty("TenantId")?.PropertyType == typeof(string);
        if (options.TenancyStyle != TenancyStyle.DatabasePerTenant
            && mapping?.IsMultiTenanted == true)
        {
            if (string.IsNullOrWhiteSpace(session.TenantId))
            {
                throw new SableBlindIndexConfigurationException(
                    $"Blind-index reindexing for multi-tenanted document type " +
                    $"'{typeof(T).FullName}' requires a tenant-scoped session.");
            }

        }

        if (options.TenancyStyle != TenancyStyle.DatabasePerTenant
            && hasTenantField
            && !string.IsNullOrWhiteSpace(session.TenantId))
        {
            var tenantField = MetadataDispatch.GetFieldName(
                typeof(T), "TenantId", options.Schema);
            parameters["reindex_tenant"] = session.TenantId;
            filters.Add($"{tenantField} = $reindex_tenant");
        }

        return filters.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", filters)}";
    }

    private static string EscapeIdentifier(string identifier) =>
        identifier.Replace("`", "``", StringComparison.Ordinal);
}

/// <summary>Summary of an explicit blind-index reindex pass.</summary>
public readonly record struct BlindIndexReindexResult(
    int DocumentsScanned,
    int TokensWritten,
    RecordId? NextCursor,
    bool HasMore);
