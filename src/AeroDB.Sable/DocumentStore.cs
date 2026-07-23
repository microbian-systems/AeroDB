using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;
using System.Threading;
using AeroDB.Sable.Internals.Cbor;
using AeroDB.Sable.LiveQuery;
using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

/// <summary>The concrete implementation of <see cref="IDocumentStore"/>. Manages a SurrealDB connection, schema initialization, projection lifecycle, and session factory. Created via <see cref="Documents.For"/> or the <c>AddAeroDB()</c> DI extension.</summary>
public class DocumentStore : IDocumentStore, ISessionFactory
{
    private readonly ILogger<DocumentStore> _logger;
    private ISurrealDbClient? _client;
    private DatabasePerTenantSelector? _tenantSelector;
    private string? _currentTenantId;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private int _initialized; // 0 = uninitialized, 1 = initializing, 2 = initialized (Interlocked-atomic)
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

    private IAeroDBAdvanced? _advanced;
    public IAeroDBAdvanced Advanced => _advanced ??= new AeroDBAdvanced(Client, Options);

    /// <summary>
    /// Async daemon for background projection processing, if events are enabled
    /// and an <see cref="AsyncDaemon"/> has been started. Set by external code
    /// when the daemon is created and started.
    /// </summary>
    public AsyncDaemon? Daemon { get; set; }

    /// <summary>
    /// Sets the tenant ID for the next session created from this store (DatabasePerTenant mode).
    /// The tenant ID is consumed on the next call to <c>QuerySessionAsync</c>,
    /// <c>OpenSessionAsync</c>, or <c>OpenSessionAsync</c>.
    /// </summary>
    public IDocumentStore WithTenant(string tenantId)
    {
        _currentTenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        return this;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Fast path: already fully initialized
        if (Volatile.Read(ref _initialized) == 2) return;

        await _initializationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A concurrent caller may have completed initialization while this caller waited.
            if (Volatile.Read(ref _initialized) == 2) return;

            Interlocked.Exchange(ref _initialized, 1);
            try
            {

        // DatabasePerTenant: skip connecting to a default database;
        // each tenant gets its own database on first session creation.
        if (Options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            _tenantSelector = new DatabasePerTenantSelector(Options);

            // Apply IConfigureAeroDB modules (manual Configurators list)
            foreach (var configurator in Options.Configurators)
                configurator.Configure(Options.ServiceProvider, Options);

            // Phase 0: IAeroSchemaBuilder — new fluent config path
            var dbPerTenantSchema = new AeroDBSchemaBuilder(Options);
            foreach (var configurator in Options.Configurators)
                configurator.Configure(dbPerTenantSchema);

            // Auto-discover and apply IConfigureAeroDB from DI (if ServiceProvider is set)
            await ApplyDiscoveredConfigurators(Options, ct).ConfigureAwait(false);

            // Apply IAsyncConfigureAeroDB modules
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
            Options.Schema.ResolveRelationships();
            EncryptionMappingValidator.Validate(Options);

            // TODO: Phase 1 — ChangeTracking for DatabasePerTenant
            // In this mode, each tenant gets its own database on first session.
            // ChangeTracking will need to be applied per-tenant during schema auto-creation.
            // See ChangeTrackingSchemaManager.ApplyAsync for the implementation.

            _logger.LogInformation("AeroDB.Sable store initialized (DatabasePerTenant mode)");
            Interlocked.Exchange(ref _initialized, 2);
            return;
        }

        var ns = Options.Namespace ?? "test";
        var db = Options.Database ?? "test";

        _logger.LogInformation("Initializing AeroDB.Sable store: Endpoint={Endpoint}, ns={Namespace}, db={Database}",
            Options.Endpoint, ns, db);

        if (Options.ClientFactory is not null)
        {
            _client = Options.ClientFactory();
            AeroDBCborOptions.ConfigureClient(_client);
            await _client.Connect(ct).ConfigureAwait(false);
            await _client.Use(ns, db, ct).ConfigureAwait(false);
        }
        else
        {
            var endpoint = Options.Endpoint;
            var surrealOptionsBuilder = new SurrealDbOptionsBuilder()
                .WithEndpoint(endpoint)
                .WithNamespace(ns)
                .WithDatabase(db);

            if (!string.IsNullOrWhiteSpace(Options.Username))
            {
                surrealOptionsBuilder.WithUsername(Options.Username);
            }

            if (!string.IsNullOrWhiteSpace(Options.Password))
            {
                surrealOptionsBuilder.WithPassword(Options.Password);
            }

            if (!string.IsNullOrWhiteSpace(Options.Token))
            {
                surrealOptionsBuilder.WithToken(Options.Token);
            }

            var surrealOptions = surrealOptionsBuilder.Build();

            _client = new SurrealDbClient(
                surrealOptions,
                configureCborOptions: AeroDBCborOptions.Configure);
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

        // Apply IConfigureAeroDB modules (manual Configurators list)
        foreach (var configurator in Options.Configurators)
            configurator.Configure(Options.ServiceProvider, Options);

        // Phase 0: IAeroSchemaBuilder — new fluent config path
        var schemaBuilder = new AeroDBSchemaBuilder(Options);
        foreach (var configurator in Options.Configurators)
            configurator.Configure(schemaBuilder);

        // Auto-discover and apply IConfigureAeroDB from DI (if ServiceProvider is set)
        await ApplyDiscoveredConfigurators(Options, ct).ConfigureAwait(false);

        // Apply IAsyncConfigureAeroDB modules (async config, e.g. satellite assemblies)
        foreach (var asyncConfigurator in Options.AsyncConfigurators)
            await asyncConfigurator.ConfigureAsync(Options, ct).ConfigureAwait(false);

        // Apply global document policies to all registered mappings
        ApplyPolicies();
        Options.Schema.ResolveRelationships();
        EncryptionMappingValidator.Validate(Options);

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
                    await schemaManager.EnsureDocumentSchemaAsync(
                        mapping.EntityType,
                        schemaSession,
                        mode: mapping.SchemaModeType,
                        fieldDefinitions: fds,
                        relationshipMappings: mapping.GetRelationshipMappings(),
                        schemaOptions: Options.Schema,
                        ct: ct,
                        enumStorage: Options.EnumStorage).ConfigureAwait(false);

                    // Ensure each configured index
                    var tableName = MetadataDispatch.GetTableName(mapping.EntityType, Options.Schema);
                    foreach (var index in mapping.Indices)
                    {
                        await schemaManager.EnsureIndexAsync(
                            schemaSession,
                            tableName,
                            index,
                            mapping.EntityType,
                            Options.Schema,
                            ct).ConfigureAwait(false);
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
                        await schemaManager.EnsureDocumentSchemaAsync(
                            mapping.EntityType,
                            schemaSession,
                            mode: mapping.SchemaModeType,
                            fieldDefinitions: fieldDefs,
                            relationshipMappings: mapping.GetRelationshipMappings(),
                            schemaOptions: Options.Schema,
                            ct: ct,
                            enumStorage: Options.EnumStorage).ConfigureAwait(false);

                        var tableName = MetadataDispatch.GetTableName(mapping.EntityType, Options.Schema);
                        foreach (var index in mapping.Indices)
                        {
                            await schemaManager.EnsureIndexAsync(
                                schemaSession,
                                tableName,
                                index,
                                mapping.EntityType,
                                Options.Schema,
                                ct).ConfigureAwait(false);
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

        // Phase 1: ChangeTracking — SurrealDB-native audit trail (CHANGEFEED + DEFINE EVENT triggers)
        {
            await using var changeTrackingSession = await _client.CreateSession(ct).ConfigureAwait(false);
            await changeTrackingSession.Use(ns, db, ct).ConfigureAwait(false);
            var changeTrackingManager = new ChangeTrackingSchemaManager(Options.LoggerFactory);
            await changeTrackingManager.ApplyAsync(Options, changeTrackingSession, ct).ConfigureAwait(false);
        }

        // Phase 2: EventStream — create event stream tables and indexes
        if (Options.EventStreamConfigs.Count > 0)
        {
            await using var eventStreamSession = await _client.CreateSession(ct).ConfigureAwait(false);
            await eventStreamSession.Use(ns, db, ct).ConfigureAwait(false);

            foreach (var (aggregateType, config) in Options.EventStreamConfigs)
            {
                _logger.LogDebug("Creating event stream table for aggregate {AggregateType}", aggregateType.Name);
                var surql = EventStreamGenerator.BuildCreateSurql(config);
                await eventStreamSession.RawQuery(surql, null, ct).ConfigureAwait(false);
            }
        }

        // Phase 3: PatchEvents — register patch-to-event rules (no DB operations)
        {
            var patchRules = new PatchRuleResolver();
            foreach (var mapping in Options.Schema.Mappings.Values)
            {
                var config = mapping.PatchEventsConfig;
                if (config is null)
                    continue;

                // Use reflection to access the generic PatchEventsConfiguration<T>.Rules property
                var rulesProperty = config.GetType().GetProperty("Rules",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (rulesProperty?.GetValue(config) is IReadOnlyList<PatchEventRule> rules)
                {
                    patchRules.RegisterRange(rules);
                    _logger.LogDebug("Registered {Count} patch event rules for {EntityType}",
                        rules.Count, mapping.EntityType.Name);
                }
            }
            Options.PatchRules = patchRules;
            _logger.LogInformation("Registered {Count} total patch event rules across all entities", patchRules.Count);
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
                    await using var rebuildSession = await OpenDefaultSessionAsync(
                        new SessionOptions { Tracking = DocumentTracking.None },
                        ct).ConfigureAwait(false);
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
            await using var seedSession = await OpenDefaultSessionAsync(
                new SessionOptions { Tracking = DocumentTracking.None },
                ct).ConfigureAwait(false);

            foreach (var seeder in Options.InitialData)
            {
                _logger.LogDebug("Running initial data seeder: {Type}", seeder.GetType().Name);
                await seeder.PopulateAsync(seedSession, ct).ConfigureAwait(false);
            }

            await seedSession.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Initial data seeding complete");
        }

        _logger.LogInformation("AeroDB.Sable store initialized successfully: ns={Namespace}, db={Database}", ns, db);

            // Mark fully initialized only after everything succeeds
            Interlocked.Exchange(ref _initialized, 2);
            }
            catch
            {
                // A failed singleton factory is not retained by Microsoft DI. Release any
                // partially connected embedded client before a subsequent resolution retries.
                var failedClient = Interlocked.Exchange(ref _client, null);
                var failedTenantSelector = _tenantSelector;
                _tenantSelector = null;
                _advanced = null;
                Options.Advanced.SurrealDbClient = null;
                Options.Advanced.CreateSessionAsync = null;

                try
                {
                    if (failedClient is not null)
                        await failedClient.DisposeAsync().ConfigureAwait(false);
                    if (failedTenantSelector is not null)
                        await failedTenantSelector.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    _logger.LogWarning(
                        cleanupException,
                        "Failed to fully dispose AeroDB resources after initialization failure");
                }

                // Reset on failure so this store instance can be retried.
                Interlocked.Exchange(ref _initialized, 0);
                throw;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        await EnsureInitialized(ct).ConfigureAwait(false);

        try
        {
            return await Client.Version(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
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
            var qs = new QuerySession(tenantClient, session, Options, DocumentTracking.None) { TenantId = tenantId, DocumentStore = this };
            _logger.LogInformation("Created QuerySession for tenant {TenantId}", tenantId);
            return qs;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var qs2 = new QuerySession(Client, defaultSession, Options, DocumentTracking.None) { DocumentStore = this };
        if (Options.TenancyStyle == TenancyStyle.Conjoined && Options.DefaultTenantId is not null)
            qs2.TenantId = Options.DefaultTenantId;
        _logger.LogInformation("Created QuerySession");
        return qs2;
    }

    public async Task<ILiveQuerySession> LiveQuerySessionAsync(CancellationToken ct = default)
    {
        await EnsureInitialized(ct).ConfigureAwait(false);

        ISurrealDbClient client;
        string? tenantId = null;
        if (Options.TenancyStyle == TenancyStyle.DatabasePerTenant)
        {
            tenantId = ResolveTenantId();
            client = await _tenantSelector!.GetOrCreateClientAsync(tenantId, ct).ConfigureAwait(false);
        }
        else
        {
            tenantId = Options.DefaultTenantId;
            client = Client;
        }

        var dbSession = await client.CreateSession(ct).ConfigureAwait(false);
        await dbSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct)
            .ConfigureAwait(false);

        return new LiveQuerySession(dbSession, Options, Options.LoggerFactory, tenantId);
    }

    public async Task<TOut> QueryAsync<TDoc, TOut>(ICompiledQuery<TDoc, TOut> query, CancellationToken ct = default)
        where TDoc : class
    {
        await using var session = await OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, ct).ConfigureAwait(false);
        return await session.QueryAsync(query, ct).ConfigureAwait(false);
    }

    Task<IQuerySession> ISessionFactory.QuerySessionAsync(CancellationToken ct)
        => QuerySessionAsync(ct);

    Task<IDocumentSession> ISessionFactory.OpenSessionAsync(CancellationToken ct)
        => OpenSessionAsync(new SessionOptions(), ct);

    Task<IDocumentSession> ISessionFactory.OpenSessionAsync(SessionOptions options, CancellationToken ct)
        => OpenSessionAsync(options, ct);

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
            var ds = new DocumentSession(tenantClient, session, Options, options) { TenantId = tenantId, DocumentStore = this };
            _logger.LogInformation("Opened session (tracking={Tracking}) for tenant {TenantId}", options.Tracking, tenantId);
            return ds;
        }

        return await OpenDefaultSessionAsync(options, ct).ConfigureAwait(false);
    }

    private async Task<IDocumentSession> OpenDefaultSessionAsync(
        SessionOptions options,
        CancellationToken ct)
    {
        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(
            Options.Namespace ?? "test",
            Options.Database ?? "test",
            ct).ConfigureAwait(false);
        var documentSession = new DocumentSession(Client, defaultSession, Options, options)
        {
            DocumentStore = this
        };

        if (options.TenantId is not null)
            documentSession.TenantId = options.TenantId;
        else if (Options.TenancyStyle == TenancyStyle.Conjoined
                 && Options.DefaultTenantId is not null)
            documentSession.TenantId = Options.DefaultTenantId;

        _logger.LogInformation("Opened session (tracking={Tracking})", options.Tracking);
        return documentSession;
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
            var ds = new DocumentSession(tenantClient, session, Options, DocumentTracking.None) { TenantId = tenantId, DocumentStore = this };
            _logger.LogInformation("Created LightweightSession (no tracking) for tenant {TenantId}", tenantId);
            return ds;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var ds2 = new DocumentSession(Client, defaultSession, Options, DocumentTracking.None) { DocumentStore = this };
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
            var ds = new DocumentSession(tenantClient, session, Options, DocumentTracking.IdentityOnly) { TenantId = tenantId, DocumentStore = this };
            _logger.LogInformation("Created DocumentSession (identity tracking) for tenant {TenantId}", tenantId);
            return ds;
        }

        var defaultSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await defaultSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var ds2 = new DocumentSession(Client, defaultSession, Options, DocumentTracking.IdentityOnly) { DocumentStore = this };
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
        if (Volatile.Read(ref _initialized) != 2)
            await InitializeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a graph query builder for traversal operations.
    /// </summary>
    public async Task<IGraphQuery<T>> GraphAsync<T>(CancellationToken ct = default) where T : class
    {
        await EnsureInitialized(ct).ConfigureAwait(false);
        var surrealSession = await Client.CreateSession(ct).ConfigureAwait(false);
        await surrealSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", ct).ConfigureAwait(false);
        var querySession = new QuerySession(Client, surrealSession, Options, DocumentTracking.None) { DocumentStore = this };
        return GraphQueryProvider.Graph<T>(querySession);
    }

    /// <summary>Start a graph traversal query. Creates an ephemeral session internally.</summary>
    [Obsolete("Use GraphAsync() to avoid sync-over-async deadlock. This will be removed in GA.")]
    public IGraphQuery<T> Graph<T>() where T : class
    {
        if (_initialized == 0)
            throw new InvalidOperationException("Store not initialized. Call InitializeAsync first.");

        var surrealSession = Client.CreateSession(DefaultCt).GetAwaiter().GetResult();
        surrealSession.Use(Options.Namespace ?? "test", Options.Database ?? "test", CancellationToken.None).GetAwaiter().GetResult();
        var querySession = new QuerySession(Client, surrealSession, Options, DocumentTracking.None) { DocumentStore = this };
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

        // Resolve IConfigureAeroDB implementations from DI and apply them,
        // skipping types already in the manual Configurators list.
        var diConfigurators = options.ServiceProvider.GetService(typeof(IEnumerable<IConfigureAeroDB>))
            as IEnumerable<IConfigureAeroDB>;
        if (diConfigurators is not null)
        {
            foreach (var configurator in diConfigurators)
            {
                if (manualConfiguratorTypes.Contains(configurator.GetType()))
                    continue;
                configurator.Configure(options.ServiceProvider, options);
            }

            // Phase 0: also apply IAeroSchemaBuilder to DI-discovered configurators
            var diSchemaBuilder = new AeroDBSchemaBuilder(options);
            foreach (var configurator in diConfigurators)
            {
                if (manualConfiguratorTypes.Contains(configurator.GetType()))
                    continue;
                configurator.Configure(diSchemaBuilder);
            }
        }

        // Same for IAsyncConfigureAeroDB
        var diAsyncConfigurators = options.ServiceProvider.GetService(typeof(IEnumerable<IAsyncConfigureAeroDB>))
            as IEnumerable<IAsyncConfigureAeroDB>;
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

            var tableName = Metadata.MetadataDispatch.GetTableName(mapping.DocumentType, Options.Schema);
            if (string.IsNullOrEmpty(tableName))
                continue;

            var deletedField = Metadata.MetadataDispatch.GetFieldName(mapping.DocumentType, nameof(ISoftDeleted.Deleted), Options.Schema);
            var deletedAtField = Metadata.MetadataDispatch.GetFieldName(mapping.DocumentType, nameof(ISoftDeleted.DeletedAt), Options.Schema);
            var surql = $"DELETE FROM `{tableName}` WHERE {deletedField} = true AND {deletedAtField} < d'{cutoff:yyyy-MM-ddTHH:mm:ssZ}';";
            await internalSession.ExecuteSqlAsync(surql, null, ct).ConfigureAwait(false);
            totalDeleted++;
        }

        return totalDeleted;
    }

    // ── Tenant-scoped BulkInsert overloads ──────────────────────

    /// <inheritdoc />
    public async Task BulkInsertAsync<T>(string tenantId, IEnumerable<T> documents, BulkInsertMode mode = BulkInsertMode.InsertsOnly, int batchSize = 1000, CancellationToken ct = default) where T : class
    {
        await using var session = (DocumentSession)await OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, ct).ConfigureAwait(false);
        session.SetTenant(tenantId);
        await session.BulkInsertAsync(documents, batchSize, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task BulkInsertDocumentsAsync(string tenantId, IEnumerable<object> documents, BulkInsertMode mode = BulkInsertMode.InsertsOnly, int batchSize = 1000, CancellationToken ct = default)
    {
        await using var session = (DocumentSession)await OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, ct).ConfigureAwait(false);
        session.SetTenant(tenantId);
        session.StoreObjects(documents);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task BulkInsertDocumentsAsync(IEnumerable<object> documents, BulkInsertMode mode = BulkInsertMode.InsertsOnly, int batchSize = 1000, CancellationToken ct = default)
    {
        await using var session = (DocumentSession)await OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, ct).ConfigureAwait(false);
        session.StoreObjects(documents);
        await session.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task BulkInsertEventsAsync(string tenantId, IEnumerable<(string StreamId, IEnumerable<object> Events)> streams, int batchSize = 1000, CancellationToken ct = default)
    {
        await using var session = (DocumentSession)await OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None }, ct).ConfigureAwait(false);
        session.SetTenant(tenantId);
        await session.Events.BulkInsertEventsAsync(streams, batchSize, ct).ConfigureAwait(false);
    }

    // ── Tenant-scoped session factories ─────────────────────────

    /// <inheritdoc />
    public async Task<IDocumentSession> IdentitySessionAsync(string tenantId, CancellationToken ct = default)
    {
        var session = await OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.IdentityOnly }, ct).ConfigureAwait(false);
        session.SetTenant(tenantId);
        return session;
    }

    /// <inheritdoc />
    public async Task<IDocumentSession> DirtyTrackedSessionAsync(string tenantId, CancellationToken ct = default)
    {
        var session = await OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.DirtyTracking }, ct).ConfigureAwait(false);
        session.SetTenant(tenantId);
        return session;
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

/// <summary>Static factory class for creating document stores. Use <c>Documents.For(configure)</c> for programmatic setup, or <c>services.AddAeroDB(configure)</c> for dependency injection integration.</summary>
public static class Documents
{
    public static IDocumentStore For(Action<StoreOptions> configure)
    {
        var options = new StoreOptions();
        configure(options);
        return new DocumentStore(options);
    }
}
