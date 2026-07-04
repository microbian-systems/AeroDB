using AeroDB.Metadata;
using SurrealDb.Net;

namespace AeroDB;

internal sealed class DaliAdvanced : IDaliAdvanced
{
    private readonly ISurrealDbClient _client;
    private readonly StoreOptions _options;
    private IDiagnostics? _diagnostics;
    private SchemaDiffer? _schemaDiffer;

    public ISurrealDbClient Client => _client;

    public IDiagnostics Diagnostics => _diagnostics ??= new DaliDiagnostics(_client);

    public DaliAdvanced(ISurrealDbClient client, StoreOptions options)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<SchemaDiff> ComputeSchemaDiffAsync(CancellationToken ct = default)
    {
        _schemaDiffer ??= new SchemaDiffer(_client, _options);
        return await _schemaDiffer.ComputeDiffAsync(ct).ConfigureAwait(false);
    }

    public async Task<ISurrealDbSession> CreateSessionAsync(CancellationToken ct = default)
    {
        // For remote connections, ForkSession. For in-memory, use client directly.
        if (_client is ISurrealDbSession session)
        {
            return await session.ForkSession(ct).ConfigureAwait(false);
        }
        return await _client.CreateSession(ct).ConfigureAwait(false);
    }

    public async Task ResetAllDataAsync(CancellationToken ct = default)
    {
        // Collect system tables and user document tables from registered mappings
        var tables = new List<string> { "mt_events", "mt_archived_streams", "mt_projection_progress" };

        foreach (var kvp in _options.Schema.Mappings)
        {
            var tableName = MetadataDispatch.GetTableName(kvp.Key);
            if (!string.IsNullOrEmpty(tableName))
                tables.Add(tableName);
        }

        tables = tables.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        await using var ds = await CreateSessionAsync(ct).ConfigureAwait(false);
        var ns = _options.Namespace ?? "test";
        var db = _options.Database ?? "test";
        await ds.Use(ns, db, ct).ConfigureAwait(false);

        foreach (var table in tables)
        {
            await ds.RawQuery($"DELETE {table};", null, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteAllDocumentsAsync<T>(CancellationToken ct = default) where T : class
    {
        await DeleteDocumentsByTypeAsync(typeof(T), ct).ConfigureAwait(false);
    }

    public async Task DeleteDocumentsByTypeAsync(Type documentType, CancellationToken ct = default)
    {
        var tableName = MetadataDispatch.GetTableName(documentType);
        if (string.IsNullOrEmpty(tableName)) return;

        var session = await CreateSessionAsync(ct).ConfigureAwait(false);
        await using (session)
        {
            var ns = _options.Namespace ?? "test";
            var db = _options.Database ?? "test";
            await session.Use(ns, db, ct).ConfigureAwait(false);
            await session.RawQuery($"DELETE FROM `{tableName}`;", null, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteDocumentsExceptAsync(Type[] preservedTypes, CancellationToken ct = default)
    {
        var preservedTableNames = new HashSet<string>(
            preservedTypes.Select(t => MetadataDispatch.GetTableName(t)),
            StringComparer.OrdinalIgnoreCase);

        var session = await CreateSessionAsync(ct).ConfigureAwait(false);
        await using (session)
        {
            var ns = _options.Namespace ?? "test";
            var db = _options.Database ?? "test";
            await session.Use(ns, db, ct).ConfigureAwait(false);

            foreach (var kvp in _options.Schema.Mappings)
            {
                var tableName = MetadataDispatch.GetTableName(kvp.Key);
                if (!string.IsNullOrEmpty(tableName) && !preservedTableNames.Contains(tableName))
                {
                    await session.RawQuery($"DELETE FROM `{tableName}`;", null, ct).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task DeleteAllEventDataAsync(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct).ConfigureAwait(false);
        await using (session)
        {
            var ns = _options.Namespace ?? "test";
            var db = _options.Database ?? "test";
            await session.Use(ns, db, ct).ConfigureAwait(false);
            await session.RawQuery("DELETE FROM mt_events;", null, ct).ConfigureAwait(false);
        }
    }

    public async Task CompletelyRemoveAsync(Type documentType, CancellationToken ct = default)
    {
        var tableName = MetadataDispatch.GetTableName(documentType);
        if (string.IsNullOrEmpty(tableName)) return;

        var session = await CreateSessionAsync(ct).ConfigureAwait(false);
        await using (session)
        {
            var ns = _options.Namespace ?? "test";
            var db = _options.Database ?? "test";
            await session.Use(ns, db, ct).ConfigureAwait(false);
            await session.RawQuery($"REMOVE TABLE `{tableName}`;", null, ct).ConfigureAwait(false);
        }
    }
}
