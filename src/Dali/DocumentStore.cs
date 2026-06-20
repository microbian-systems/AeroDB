using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;

namespace Dali;

public class DocumentStore : IDocumentStore
{
    private readonly ILogger<DocumentStore> _logger;
    private ISurrealDbClient? _client;
    private DatabasePerTenantSelector? _tenantSelector;
    private string? _currentTenantId;
    private bool _initialized;
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
        if (_initialized) return;
        _initialized = true;

        // DatabasePerTenant: skip connecting to a default database;
        // each tenant gets its own database on first session creation.
        if (Options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            _tenantSelector = new DatabasePerTenantSelector(Options);

            // Apply IConfigureDali modules
            foreach (var configurator in Options.Configurators)
                configurator.Configure(Options);

            // Inject logger factory into projections that support it
            if (Options.LoggerFactory is not null)
            {
                foreach (var projection in Options.Projections)
                {
                    if (projection is ILoggableProjection loggable)
                        loggable.SetLoggerFactory(Options.LoggerFactory);
                }
            }

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

        // Apply IConfigureDali modules
        foreach (var configurator in Options.Configurators)
            configurator.Configure(Options);

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
                    await schemaManager.EnsureDocumentSchemaAsync(mapping.EntityType, schemaSession, mapping.SchemaModeType, ct).ConfigureAwait(false);

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
                        await schemaManager.EnsureDocumentSchemaAsync(mapping.EntityType, schemaSession, mapping.SchemaModeType, ct).ConfigureAwait(false);

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

        // Inject logger factory into projections that support it
        if (Options.LoggerFactory is not null)
        {
            foreach (var projection in Options.Projections)
            {
                if (projection is ILoggableProjection loggable)
                    loggable.SetLoggerFactory(Options.LoggerFactory);
            }
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
            var qs = new QuerySession(tenantClient, session, Options) { TenantId = tenantId };
            _logger.LogInformation("Created QuerySession for tenant {TenantId}", tenantId);
            return qs;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var qs2 = new QuerySession(Client, defaultSession, Options);
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            qs2.TenantId = Options.DefaultTenantId;
        _logger.LogInformation("Created QuerySession");
        return qs2;
    }

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
            var ds = new DocumentSession(tenantClient, session, Options, isDirtyTracking: false) { TenantId = tenantId };
            _logger.LogInformation("Created LightweightSession (no tracking) for tenant {TenantId}", tenantId);
            return ds;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var ds2 = new DocumentSession(Client, defaultSession, Options, isDirtyTracking: false);
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            ds2.TenantId = Options.DefaultTenantId;
        _logger.LogInformation("Created LightweightSession (no tracking)");
        return ds2;
    }

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
            var ds = new DocumentSession(tenantClient, session, Options, isDirtyTracking: true) { TenantId = tenantId };
            _logger.LogInformation("Created DocumentSession (dirty tracking) for tenant {TenantId}", tenantId);
            return ds;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var ds2 = new DocumentSession(Client, defaultSession, Options, isDirtyTracking: true);
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            ds2.TenantId = Options.DefaultTenantId;
        _logger.LogInformation("Created DocumentSession (dirty tracking)");
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
        if (!_initialized)
            await InitializeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Start a graph traversal query. Creates an ephemeral session internally.</summary>
    public IGraphQuery<T> Graph<T>() where T : class
    {
        if (!_initialized)
            throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

        var surrealSession = Client.CreateSession(DefaultCt).GetAwaiter().GetResult();
        surrealSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", CancellationToken.None).GetAwaiter().GetResult();
        var querySession = new QuerySession(Client, surrealSession, Options);
        return GraphQueryProvider.Graph<T>(querySession);
    }

    private static readonly CancellationToken DefaultCt = CancellationToken.None;

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

public static class Documents
{
    public static IDocumentStore For(Action<StoreOptions> configure)
    {
        var options = new StoreOptions();
        configure(options);
        return new DocumentStore(options);
    }
}
