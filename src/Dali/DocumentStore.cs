using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using System.Threading;

namespace Dali;

/// <summary>The concrete implementation of <see cref="IDocumentStore"/>. Manages a SurrealDB connection, schema initialization, projection lifecycle, and session factory. Created via <see cref="Documents.For"/> or the <c>AddDali()</c> DI extension.</summary>
public class DocumentStore : IDocumentStore
{
    private readonly ILogger<DocumentStore> _logger;
    private ISurrealDbClient? _client;
    private DatabasePerTenantSelector? _tenantSelector;
    private string? _currentTenantId;
    private int _initialized; // 0 = false, 1 = true (Interlocked-atomic)
    private bool _disposed;

    public DocumentStore(StoreOptions options)
    {
        Options = options;
        _logger = options.LoggerFactory?.CreateLogger<DocumentStore>()
            ?? NullLogger<DocumentStore>.Instance;
    }

    public StoreOptions Options { get; }
    public ISurrealDbClient Client => _client
        ?? throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

    private IDaliAdvanced? _advanced;
    public IDaliAdvanced Advanced => _advanced ??= new DaliAdvanced(Client, Options);

    /// <summary>
    /// Async daemon for background projection processing, if events are enabled
    /// and an <see cref="AsyncDaemon"/> has been started. Set by external code
    /// when the daemon is created and started.
    /// </summary>
    public AsyncDaemon? Daemon { get; set; }

    /// <summary>
    /// Sets the tenant ID for the next session created from this store (DatabasePerTenant mode).
    /// The tenant ID is consumed on the next call to <c>QuerySessionAsync</c>,
    /// <c>LightweightSessionAsync</c>, or <c>DocumentSessionAsync</c>.
    /// </summary>
    public IDocumentStore WithTenant(string tenantId)
    {
        _currentTenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        return this;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;

        // DatabasePerTenant: skip connecting to a default database;
        // each tenant gets its own database on first session creation.
        if (Options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            _tenantSelector = new DatabasePerTenantSelector(Options);

            // Apply IConfigureDali modules (manual Configurators list)
            foreach (var configurator in Options.Configurators)
                configurator.Configure(Options.ServiceProvider, Options);

            // Auto-discover and apply IConfigureDali from DI (if ServiceProvider is set)
            await ApplyDiscoveredConfigurators(Options, ct).ConfigureAwait(false);

            // Apply IAsyncConfigureDali modules
            foreach (var asyncConfigurator in Options.AsyncConfigurators)
                await asyncConfigurator.ConfigureAsync(Options, ct).ConfigureAwait(false);

            // Inject logger factory into projections that support it
            if (Options.LoggerFactory is not null)
            {
                foreach (var projection in Options.Projections)
                {
                    if (projection is ILoggableProjection loggable)
                        loggable.SetLoggerFactory(Options.LoggerFactory);
                }
            }

            // Apply global document policies to all registered mappings
            ApplyPolicies();

            _logger.LogInformation("Dali store initialized (DatabasePerTenant mode)");
            return;
        }

        var ns = Options.Namespace ?? "test";
        var db = Options.Database ?? "test";

        _logger.LogInformation("Initializing Dali store: Endpoint={Endpoint}, ns={Namespace}, db={Database}",
            Options.Endpoint, ns, db);

        if (Options.ClientFactory is not null)
        {
            _client = Options.ClientFactory();
            await _client.Connect(ct).ConfigureAwait(false);
            await _client.Use(ns, db, ct).ConfigureAwait(false);
        }
        else
        {
            var endpoint = Options.Endpoint;
            var user = Options.Username ?? "root";
            var pass = Options.Password ?? "root";

            var surrealOptions = new SurrealDbOptionsBuilder()
                .WithEndpoint(endpoint)
                .WithNamespace(ns)
                .WithDatabase(db)
                .WithUsername(user)
                .WithPassword(pass)
                .Build();

            _client = new SurrealDbClient(surrealOptions);
            await _client.Connect(ct).ConfigureAwait(false);
            await _client.Use(ns, db, ct).ConfigureAwait(false);
        }

        // Populate advanced SDK access
        Options.Advanced.SurrealDbClient = _client;
        Options.Advanced.CreateSessionAsync = async (ct) =>
        {
            if (_client is ISurrealDbSession session)
                return await session.ForkSession(ct).ConfigureAwait(false);
            return await _client.CreateSession(ct).ConfigureAwait(false);
        };

        // Apply IConfigureDali modules (manual Configurators list)
        foreach (var configurator in Options.Configurators)
            configurator.Configure(Options.ServiceProvider, Options);

        // Auto-discover and apply IConfigureDali from DI (if ServiceProvider is set)
        await ApplyDiscoveredConfigurators(Options, ct).ConfigureAwait(false);

        // Apply IAsyncConfigureDali modules (async config, e.g. satellite assemblies)
        foreach (var asyncConfigurator in Options.AsyncConfigurators)
            await asyncConfigurator.ConfigureAsync(Options, ct).ConfigureAwait(false);

        // Apply global document policies to all registered mappings
        ApplyPolicies();

        var schemaManager = new SchemaManager(Options.LoggerFactory);
        var triggerManager = new EventTriggerManager(Options.LoggerFactory);
        var functionManager = new FunctionManager(Options.LoggerFactory);

        // Auto-create document schemas if configured (includes analyzers, tables, and indexes)
        if (Options.Schema.AutoCreate && Options.Schema.Mappings.Count > 0)
        {
            // ── Default database (null schema) ──
            var defaultMappings = Options.Schema.Mappings.Values.Where(m => m.SchemaName is null).ToList();
            if (defaultMappings.Count > 0)
            {
                await using var schemaSession = await _client.CreateSession(ct).ConfigureAwait(false);
                await schemaSession.Use(ns, db, ct).ConfigureAwait(false);

                // Ensure analyzers before indexes (analyzers must exist before indexes referencing them)
                if (Options.Schema.Analyzers.Analyzers.Count > 0)
                {
                    _logger.LogInformation("Applying {Count} analyzers", Options.Schema.Analyzers.Analyzers.Count);
                    await schemaManager.EnsureAnalyzersAsync(schemaSession, Options.Schema.Analyzers, ct).ConfigureAwait(false);
                }

                foreach (var mapping in defaultMappings)
                {
                    // Ensure table schema (DEFINE TABLE + fields) with the configured schema mode
                    var fds = mapping.GetFieldDefinitions();
                    await schemaManager.EnsureDocumentSchemaAsync(mapping.EntityType, schemaSession, mode: mapping.SchemaModeType, fieldDefinitions: fds, ct: ct).ConfigureAwait(false);

                    // Ensure each configured index
                    var tableName = SchemaManager.Snake(mapping.EntityType.Name);
                    foreach (var index in mapping.Indices)
                    {
                        await schemaManager.EnsureIndexAsync(schemaSession, tableName, index, ct).ConfigureAwait(false);
                    }
                }
            }

            // ── Per-schema databases (non-null SchemaName) ──
            var schemaNames = Options.Schema.Mappings.Values
                .Select(m => m.SchemaName)
                .Where(s => s is not null)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (schemaNames.Count > 0)
            {
                foreach (var schemaName in schemaNames)
                {
                    await using var schemaSession = await _client.CreateSession(ct).ConfigureAwait(false);
                    await schemaSession.Use(ns, db, ct).ConfigureAwait(false);

                    // Optionally create the database if it doesn't exist
                    if (Options.Schema.AutoCreateDatabases)
                    {
                        await schemaManager.EnsureDatabaseAsync(schemaSession, schemaName!, ct).ConfigureAwait(false);
                    }

                    // Switch to the schema database
                    await schemaSession.Use(ns, schemaName!, ct).ConfigureAwait(false);

                    // Process all mappings for this schema
                    foreach (var kvp in Options.Schema.Mappings.Where(m => m.Value.SchemaName == schemaName))
                    {
                        var mapping = kvp.Value;
                        var fieldDefs = mapping.GetFieldDefinitions();
                        await schemaManager.EnsureDocumentSchemaAsync(mapping.EntityType, schemaSession, mode: mapping.SchemaModeType, fieldDefinitions: fieldDefs, ct: ct).ConfigureAwait(false);

                        var tableName = SchemaManager.Snake(mapping.EntityType.Name);
                        foreach (var index in mapping.Indices)
                        {
                            await schemaManager.EnsureIndexAsync(schemaSession, tableName, index, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        // Auto-create edge (relation) table schemas if configured
        if (Options.Schema.AutoCreate && Options.Schema.EdgeMappings.Count > 0)
        {
            await using var edgeSession = await _client.CreateSession(ct).ConfigureAwait(false);
            await edgeSession.Use(ns, db, ct).ConfigureAwait(false);

            foreach (var edgeMapping in Options.Schema.EdgeMappings)
                await schemaManager.EnsureEdgeSchemaAsync(edgeSession, edgeMapping, ct).ConfigureAwait(false);
        }

        // Auto-create event schema if events are enabled
        if (Options.Events.Enabled)
        {
            await schemaManager.EnsureEventSchemaAsync(_client, ns, db, ct).ConfigureAwait(false);
        }

        // Auto-create SurrealDB native event triggers
        if (Options.Events.Triggers.AutoCreateTriggers && Options.Events.Triggers.Triggers.Count > 0)
        {
            await using var triggerSession = await _client.CreateSession(ct).ConfigureAwait(false);
            await triggerSession.Use(ns, db, ct).ConfigureAwait(false);

            foreach (var trigger in Options.Events.Triggers.Triggers)
            {
                // Resolve table name via metadata if not hard-coded
                if (string.IsNullOrEmpty(trigger.Table) && !string.IsNullOrEmpty(trigger.Name))
                    _logger.LogWarning("Event trigger {Name} has no table specified — skipping", trigger.Name);
                else
                    await triggerManager.EnsureTriggerAsync(triggerSession, trigger, ct).ConfigureAwait(false);
            }
        }

        // Auto-create user-defined functions
        var functionOpts = Options.Functions;
        if (functionOpts.AutoCreateFunctions && functionOpts.Functions.Count > 0)
        {
            await using var funcSession = await _client.CreateSession(ct).ConfigureAwait(false);
            await funcSession.Use(ns, db, ct).ConfigureAwait(false);

            foreach (var function in functionOpts.Functions)
            {
                await functionManager.EnsureFunctionAsync(funcSession, function, ct).ConfigureAwait(false);
            }
        }

        // Auto-create pre-computed/aggregate views (DEFINE TABLE ... AS SELECT ...)
        if (Options.Views.Configurations.Count > 0)
        {
            // ── Default database views (null SchemaName) ──
            var defaultViewRegs = Options.Views.Configurations
                .Where(r => r.Definition.SchemaName is null)
                .ToList();

            if (defaultViewRegs.Count > 0)
            {
                await using var viewSession = await _client.CreateSession(ct).ConfigureAwait(false);
                await viewSession.Use(ns, db, ct).ConfigureAwait(false);

                _logger.LogInformation("Applying {Count} views to default database",
                    defaultViewRegs.Count);
                foreach (var reg in defaultViewRegs)
                {
                    await reg.ExecuteAsync(viewSession, schemaManager, ct).ConfigureAwait(false);
                }
            }

            // ── Per-schema database views (non-null SchemaName) ──
            var viewSchemaNames = Options.Views.Configurations
                .Select(r => r.Definition.SchemaName)
                .Where(s => s is not null)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (viewSchemaNames.Count > 0)
            {
                foreach (var schemaName in viewSchemaNames)
                {
                    await using var schemaViewSession = await _client.CreateSession(ct).ConfigureAwait(false);
                    await schemaViewSession.Use(ns, db, ct).ConfigureAwait(false);

                    // Optionally create the database if it doesn't exist
                    if (Options.Schema.AutoCreateDatabases)
                    {
                        await schemaManager.EnsureDatabaseAsync(schemaViewSession, schemaName!, ct).ConfigureAwait(false);
                    }

                    // Switch to the schema database
                    await schemaViewSession.Use(ns, schemaName!, ct).ConfigureAwait(false);

                    var schemaViewRegs = Options.Views.Configurations
                        .Where(r => r.Definition.SchemaName == schemaName)
                        .ToList();
                    _logger.LogInformation("Applying {Count} views to schema {Schema}",
                        schemaViewRegs.Count, schemaName);
                    foreach (var reg in schemaViewRegs)
                    {
                        await reg.ExecuteAsync(schemaViewSession, schemaManager, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        // ── Auth schema: DEFINE ACCESS / TOKEN / SCOPE ──────────
        await using var authSession = await _client.CreateSession(ct).ConfigureAwait(false);
        await authSession.Use(ns, db, ct).ConfigureAwait(false);

        if (Options.Schema.Accesses.Count > 0)
        {
            _logger.LogInformation("Ensuring {Count} access definitions", Options.Schema.Accesses.Count);
            await schemaManager.EnsureAccessesAsync(authSession, Options.Schema.Accesses, ct).ConfigureAwait(false);
        }
        if (Options.Schema.Tokens.Count > 0)
        {
            _logger.LogInformation("Ensuring {Count} token definitions", Options.Schema.Tokens.Count);
            await schemaManager.EnsureTokensAsync(authSession, Options.Schema.Tokens, ct).ConfigureAwait(false);
        }
        if (Options.Schema.Scopes.Count > 0)
        {
            _logger.LogInformation("Ensuring {Count} scope definitions", Options.Schema.Scopes.Count);
            await schemaManager.EnsureScopesAsync(authSession, Options.Schema.Scopes, ct).ConfigureAwait(false);
        }

        // Register composite projections (sub-projections are dispatched by the composite)
        foreach (var comp in Options.ProjectionBuild.CompositeProjections)
            Options.Projections.Add(comp);

        // Inject logger factory into projections that support it
        if (Options.LoggerFactory is not null)
        {
            foreach (var projection in Options.Projections)
            {
                if (projection is ILoggableProjection loggable)
                    loggable.SetLoggerFactory(Options.LoggerFactory);
            }
        }

        // Ensure projection progress state table if configured
        if (Options.ProjectionBuild.EnsureStateTable)
        {
            await using var stateSession = await _client.CreateSession(ct).ConfigureAwait(false);
            await stateSession.Use(ns, db, ct).ConfigureAwait(false);
            await schemaManager.EnsureProjectionStateTableAsync(stateSession, ct).ConfigureAwait(false);
        }

        // Rebuild projections on startup if configured
        if (Options.ProjectionBuild.RebuildOnStartup)
        {
            foreach (var projection in Options.Projections)
            {
                var name = projection.GetType().Name;
                var names = Options.ProjectionBuild.RebuildProjectionNames;
                if (names.Length == 0 || names.Contains(name))
                {
                    _logger.LogInformation("Rebuilding projection {ProjectionName}...", name);
#pragma warning disable CS0618
                    await using var rebuildSession = await LightweightSessionAsync(ct).ConfigureAwait(false);
#pragma warning restore CS0618
                    await projection.RebuildAsync(rebuildSession, ct).ConfigureAwait(false);
                    await rebuildSession.SaveChangesAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation("Projection {ProjectionName} rebuilt successfully.", name);
                }
            }
        }

        // Run initial data seeders
        if (Options.InitialData.Count > 0)
        {
            _logger.LogInformation("Running {Count} initial data seeders", Options.InitialData.Count);
#pragma warning disable CS0618
            await using var seedSession = await LightweightSessionAsync(ct).ConfigureAwait(false);
#pragma warning restore CS0618

            foreach (var seeder in Options.InitialData)
            {
                _logger.LogDebug("Running initial data seeder: {Type}", seeder.GetType().Name);
                await seeder.PopulateAsync(seedSession, ct).ConfigureAwait(false);
            }

            await seedSession.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Initial data seeding complete");
        }

        _logger.LogInformation("Dali store initialized successfully: ns={Namespace}, db={Database}", ns, db);
    }

    public async Task<IQuerySession> QuerySessionAsync(CancellationToken ct = default)
    {
        await EnsureInitialized(ct).ConfigureAwait(false);

        if (Options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            var tenantId = ResolveTenantId();
            var tenantClient = await _tenantSelector!.GetOrCreateClientAsync(tenantId, ct).ConfigureAwait(false);
            var session = await tenantClient.CreateSession(ct).ConfigureAwait(false);
            var dbName = $"{Options.Namespace ?? "test"}_{tenantId}";
            await session.Use(Options.Namespace ?? "test", dbName, ct).ConfigureAwait(false);
            var qs = new QuerySession(tenantClient, session, Options, DocumentTracking.None) { TenantId = tenantId };
            _logger.LogInformation("Created QuerySession for tenant {TenantId}", tenantId);
            return qs;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var qs2 = new QuerySession(Client, defaultSession, Options, DocumentTracking.None);
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            qs2.TenantId = Options.DefaultTenantId;
        _logger.LogInformation("Created QuerySession");
        return qs2;
    }

    public async Task<TOut> QueryAsync<TDoc, TOut>(ICompiledQuery<TDoc, TOut> query, CancellationToken ct = default)
        where TDoc : class
    {
        await using var session = await OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, ct).ConfigureAwait(false);
        return await session.QueryAsync(query, ct).ConfigureAwait(false);
    }

    public async Task<IDocumentSession> OpenSessionAsync(SessionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await EnsureInitialized(ct).ConfigureAwait(false);

        if (Options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            var tenantId = options.TenantId ?? ResolveTenantId();
            var tenantClient = await _tenantSelector!.GetOrCreateClientAsync(tenantId, ct).ConfigureAwait(false);
            var session = await tenantClient.CreateSession(ct).ConfigureAwait(false);
            var dbName = $"{Options.Namespace ?? "test"}_{tenantId}";
            await session.Use(Options.Namespace ?? "test", dbName, ct).ConfigureAwait(false);
            var ds = new DocumentSession(tenantClient, session, Options, options) { TenantId = tenantId };
            _logger.LogInformation("Opened session (tracking={Tracking}) for tenant {TenantId}", options.Tracking, tenantId);
            return ds;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var ds2 = new DocumentSession(Client, defaultSession, Options, options);

        if (options.TenantId is not null)
            ds2.TenantId = options.TenantId;
        else if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            ds2.TenantId = Options.DefaultTenantId;

        _logger.LogInformation("Opened session (tracking={Tracking})", options.Tracking);
        return ds2;
    }

    [Obsolete("Use OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }) instead.")]
    public async Task<IDocumentSession> LightweightSessionAsync(CancellationToken ct = default)
    {
        await EnsureInitialized(ct).ConfigureAwait(false);

        if (Options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            var tenantId = ResolveTenantId();
            var tenantClient = await _tenantSelector!.GetOrCreateClientAsync(tenantId, ct).ConfigureAwait(false);
            var session = await tenantClient.CreateSession(ct).ConfigureAwait(false);
            var dbName = $"{Options.Namespace ?? "test"}_{tenantId}";
            await session.Use(Options.Namespace ?? "test", dbName, ct).ConfigureAwait(false);
            var ds = new DocumentSession(tenantClient, session, Options, DocumentTracking.None) { TenantId = tenantId };
            _logger.LogInformation("Created LightweightSession (no tracking) for tenant {TenantId}", tenantId);
            return ds;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var ds2 = new DocumentSession(Client, defaultSession, Options, DocumentTracking.None);
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            ds2.TenantId = Options.DefaultTenantId;
        _logger.LogInformation("Created LightweightSession (no tracking)");
        return ds2;
    }

    [Obsolete("Use OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.IdentityOnly }) instead.")]
    public async Task<IDocumentSession> DocumentSessionAsync(CancellationToken ct = default)
    {
        await EnsureInitialized(ct).ConfigureAwait(false);

        if (Options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            var tenantId = ResolveTenantId();
            var tenantClient = await _tenantSelector!.GetOrCreateClientAsync(tenantId, ct).ConfigureAwait(false);
            var session = await tenantClient.CreateSession(ct).ConfigureAwait(false);
            var dbName = $"{Options.Namespace ?? "test"}_{tenantId}";
            await session.Use(Options.Namespace ?? "test", dbName, ct).ConfigureAwait(false);
            var ds = new DocumentSession(tenantClient, session, Options, DocumentTracking.IdentityOnly) { TenantId = tenantId };
            _logger.LogInformation("Created DocumentSession (identity tracking) for tenant {TenantId}", tenantId);
            return ds;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var ds2 = new DocumentSession(Client, defaultSession, Options, DocumentTracking.IdentityOnly);
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            ds2.TenantId = Options.DefaultTenantId;
        _logger.LogInformation("Created DocumentSession (identity tracking)");
        return ds2;
    }

    private string ResolveTenantId()
    {
        var tenantId = _currentTenantId ?? Options.DefaultTenantId;
        _currentTenantId = null; // consume and reset
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException(
                "DatabasePerTenant requires a TenantId. Call WithTenant() or set DefaultTenantId.");
        return tenantId!;
    }

    private async Task EnsureInitialized(CancellationToken ct)
    {
        if (_initialized == 0)
            await InitializeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Start a graph traversal query. Creates an ephemeral session internally.</summary>
    public IGraphQuery<T> Graph<T>() where T : class
    {
        if (_initialized == 0)
            throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

        var surrealSession = Client.CreateSession(DefaultCt).GetAwaiter().GetResult();
        surrealSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", CancellationToken.None).GetAwaiter().GetResult();
        var querySession = new QuerySession(Client, surrealSession, Options, DocumentTracking.None);
        return GraphQueryProvider.Graph<T>(querySession);
    }

    private void ApplyPolicies()
    {
        foreach (var kvp in Options.Schema.Mappings)
        {
            foreach (var policy in Options.Policies.RegisteredPolicies)
                policy.Apply(kvp.Value);
        }
    }

    private static async Task ApplyDiscoveredConfigurators(StoreOptions options, CancellationToken ct)
    {
        if (options.ServiceProvider is null) return;

        // Build a set of configurator types already in the manual Configurators list.
        // When the same type is registered via DI, we skip it to avoid double-application
        // even if it's a different instance. However, multiple DI registrations of the same
        // type with different instances (e.g. modular configurators) are all applied.
        var manualConfiguratorTypes = new HashSet<Type>();
        foreach (var c in options.Configurators)
            manualConfiguratorTypes.Add(c.GetType());

        var manualAsyncConfiguratorTypes = new HashSet<Type>();
        foreach (var c in options.AsyncConfigurators)
            manualAsyncConfiguratorTypes.Add(c.GetType());

        // Resolve IConfigureDali implementations from DI and apply them,
        // skipping types already in the manual Configurators list.
        var diConfigurators = options.ServiceProvider.GetService(typeof(IEnumerable<IConfigureDali>))
            as IEnumerable<IConfigureDali>;
        if (diConfigurators is not null)
        {
            foreach (var configurator in diConfigurators)
            {
                if (manualConfiguratorTypes.Contains(configurator.GetType()))
                    continue;
                configurator.Configure(options.ServiceProvider, options);
            }
        }

        // Same for IAsyncConfigureDali
        var diAsyncConfigurators = options.ServiceProvider.GetService(typeof(IEnumerable<IAsyncConfigureDali>))
            as IEnumerable<IAsyncConfigureDali>;
        if (diAsyncConfigurators is not null)
        {
            foreach (var asyncCfg in diAsyncConfigurators)
            {
                if (manualAsyncConfiguratorTypes.Contains(asyncCfg.GetType()))
                    continue;
                await asyncCfg.ConfigureAsync(options, ct).ConfigureAwait(false);
            }
        }
    }

    private static readonly CancellationToken DefaultCt = CancellationToken.None;

    public async Task<long> CleanDeletedDocumentsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var internalSession = await OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, ct).ConfigureAwait(false);
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        long totalDeleted = 0;

        foreach (var kvp in Options.Schema.Mappings)
        {
            var mapping = kvp.Value;

            // Only clean tables that implement ISoftDeleted
            if (!typeof(ISoftDeleted).IsAssignableFrom(mapping.DocumentType))
                continue;

            var tableName = Metadata.MetadataDispatch.GetTableName(mapping.DocumentType);
            if (string.IsNullOrEmpty(tableName))
                continue;

            // Use PascalCase field names matching the C# properties (per CBOR convention):
            // Deleted = true AND DeletedAt < cutoff
            var surql = $"DELETE FROM `{tableName}` WHERE Deleted = true AND DeletedAt < d'{cutoff:yyyy-MM-ddTHH:mm:ssZ}';";
            await internalSession.ExecuteSqlAsync(surql, null, ct).ConfigureAwait(false);
            totalDeleted++;
        }

        return totalDeleted;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);
        if (_tenantSelector is not null)
            await _tenantSelector.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Static factory class for creating document stores. Use <c>Documents.For(configure)</c> for programmatic setup, or <c>services.AddDali(configure)</c> for dependency injection integration.</summary>
public static class Documents
{
    public static IDocumentStore For(Action<StoreOptions> configure)
    {
        var options = new StoreOptions();
        configure(options);
        return new DocumentStore(options);
    }
}
