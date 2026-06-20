using Dali.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;

namespace Dali;

/// <summary>
/// Query provider for compiled queries — uses pre-parsed SurrealQL from a <see cref="CompiledQuery{T}"/>
/// without re-walking the LINQ expression tree.
/// </summary>
public class CompiledQueryProvider<T> where T : class
{
    private readonly IQuerySession _session;
    private readonly ISurrealDbSession _surrealSession;
    private readonly CompiledQuery<T> _compiled;
    private readonly ILogger<CompiledQueryProvider<T>> _logger;
    private readonly string? _tenantId;

    /// <summary>
    /// Creates a new compiled query provider.
    /// </summary>
    /// <param name="session">The query session used for execution.</param>
    /// <param name="compiled">The compiled query containing cached translation results.</param>
    public CompiledQueryProvider(IQuerySession session, CompiledQuery<T> compiled)
    {
        _session = session;
        _compiled = compiled;

        // Access the underlying SurrealDB session via the base class
        var internalSession = (InternalSessionBase)session;
        _surrealSession = internalSession.Session;
        _tenantId = session.TenantId;

        var loggerFactory = internalSession.StoreOptions.LoggerFactory;
        _logger = loggerFactory?.CreateLogger<CompiledQueryProvider<T>>()
            ?? NullLogger<CompiledQueryProvider<T>>.Instance;
    }

    /// <summary>
    /// Executes the compiled query and returns all results as a list.
    /// </summary>
    public async Task<List<T>> ToListAsync(CancellationToken ct = default)
    {
        var result = PrepareResult();
        var surql = result.ToSurrealQL();
        _logger.LogDebug("Compiled ToListAsync executing: {SurrealQL}", surql);

        var response = await _surrealSession.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            return raw ?? [];
        }

        return [];
    }

    /// <summary>
    /// Executes the compiled query with LIMIT 1 and returns the first result or <c>default</c>.
    /// </summary>
    public async Task<T?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var result = PrepareResult();
        result.Limit = 1;
        var surql = result.ToSurrealQL();
        _logger.LogDebug("Compiled FirstOrDefaultAsync executing: {SurrealQL}", surql);

        var response = await _surrealSession.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null && raw.Count > 0)
                return raw[0];
        }

        return default;
    }

    /// <summary>
    /// Clones the cached query result and applies tenant filtering if the session is tenant-scoped.
    /// </summary>
    private SurrealQueryResult PrepareResult()
    {
        var result = _compiled.QueryResult.Clone();

        // Apply tenant filter at execution time (same approach as SurrealQueryProvider)
        if (!string.IsNullOrEmpty(_tenantId) && HasTenantProperty(typeof(T)))
        {
            result.Where.Add($"TenantId = '{_tenantId.Replace("'", "\\'")}'");
            _logger.LogDebug("Tenant filter applied on compiled query: {TenantId}", _tenantId);
        }

        return result;
    }

    private static bool HasTenantProperty(Type type)
        => MetadataDispatch.HasTenantId(type);
}
