using System.Linq.Expressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Polly;
using SurrealDb.Net;
using SurrealDb.Net.Models;

namespace AeroDB;

/// <summary>The main configuration object for a AeroDB document store. Configures database connection settings, schema generation mode, projections, event sourcing, multi-tenancy, and all other store behaviors.</summary>
public class StoreOptions : IReadOnlyStoreOptions
{
    public string Endpoint { get; set; } = "http://localhost:8000";
    public string? Namespace { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Token { get; set; }

    public SchemaOptions Schema { get; } = new();

    private DocumentPolicies? _policies;

    /// <summary>
    /// Global policy system for configuring all document mappings.
    /// Use <c>Policies.ForAllDocuments()</c> to apply conventions across all mappings,
    /// or <c>Policies.ForDocumentsOfType&lt;T&gt;()</c> for type-specific policies.
    /// </summary>
    public DocumentPolicies Policies => _policies ??= new DocumentPolicies(this);

    public TenancyStyle TenancyStyle { get; set; }
    public string? DefaultTenantId { get; set; }

    /// <summary>
    /// Controls whether tenant IDs are treated as case-sensitive or case-insensitive.
    /// Defaults to <see cref="TenantIdStyle.CaseSensitive"/>.
    /// </summary>
    public TenantIdStyle TenantIdStyle { get; set; } = TenantIdStyle.CaseSensitive;

    public EventSourcingOptions Events { get; } = new();

    /// <summary>
    /// Document hierarchy mappings for polymorphic queries.
    /// Maps a base document type to its registered subclasses.
    /// </summary>
    internal Dictionary<Type, DocumentHierarchy> Hierarchies { get; } = new();

    /// <summary>
    /// Register a base type for polymorphic document queries.
    /// <see cref="DocumentHierarchy.AddSubClass{T}"/> to register derived types.
    /// </summary>
    public DocumentHierarchy HierarchyFor<TBase>() where TBase : SurrealDb.Net.Models.Record
    {
        if (Hierarchies.TryGetValue(typeof(TBase), out var existing))
            return existing;
        var hierarchy = new DocumentHierarchy(typeof(TBase));
        Hierarchies[typeof(TBase)] = hierarchy;
        return hierarchy;
    }

    /// <summary>
    /// Configuration for SurrealDB user-defined functions (DEFINE FUNCTION).
    /// Functions are created during store initialization.
    /// </summary>
    public FunctionOptions Functions { get; } = new();

    /// <summary>
    /// Registered document session listeners. Called in order during SaveChangesAsync.
    /// </summary>
    public List<IDocumentSessionListener> Listeners { get; } = new();

    /// <summary>
    /// Daemon-level change listeners invoked before/after projection commits.
    /// </summary>
    public List<IChangeListener> ChangeListeners { get; } = new();

    /// <summary>
    /// When true, documents with an <see cref="IVersioned"/> version field or
    /// a property decorated with <see cref="VersionAttribute"/> are protected
    /// against lost updates. Before saving a modified document, AeroDB checks that
    /// the current database version matches the version captured when the document
    /// was loaded or stored. If the versions differ, a <see cref="ConcurrencyException"/>
    /// is thrown.
    /// </summary>
    /// <summary>Gets or sets the default document tracking mode for sessions. Defaults to <see cref="DocumentTracking.None"/>.</summary>
    public DocumentTracking Tracking { get; set; } = DocumentTracking.None;

    public bool UseOptimisticConcurrency { get; set; }

    public Func<ISurrealDbClient>? ClientFactory { get; set; }

    /// <summary>
    /// Logger factory for creating typed loggers throughout the AeroDB stack.
    /// If null, <c>NullLogger{T}</c> is used everywhere (no-op).
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Minimum log level for AeroDB library log messages.
    /// </summary>
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Registered projections (inline and async).
    /// </summary>
    public ProjectionCollection Projections { get; } = new();

    /// <summary>
    /// EF Core projections that write through a DbContext.
    /// </summary>
    public List<IProjection> EfCoreProjections { get; } = new();

    /// <summary>
    /// Max items buffered in the live query channel before backpressure is applied.
    /// Default: 4096.
    /// </summary>
    public int LiveQueryChannelCapacity { get; set; } = 4096;

    /// <summary>
    /// Behavior when the live query channel is full.
    /// Default: Wait (block writer until space is available).
    /// </summary>
    public BoundedChannelFullMode LiveQueryChannelFullMode { get; set; } = BoundedChannelFullMode.Wait;

    /// <summary>
    /// When true (default), queries automatically filter out soft-deleted documents
    /// (those where <c>Deleted = true</c>). Set to false for admin views that need
    /// to include soft-deleted records.
    /// Soft-delete behavior always applies to entities implementing <see cref="ISoftDeleted"/>
    /// regardless of this setting — only the query auto-filter is affected.
    /// </summary>
    public bool SoftDeleteEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the <see cref="System.Text.Json.JsonSerializerOptions"/> used for
    /// entity serialization. If not set, defaults are used with camelCase naming.
    /// </summary>
    public System.Text.Json.JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// Convenience method to configure custom serializer options.
    /// </summary>
    public void ConfigureSerializer(Action<System.Text.Json.JsonSerializerOptions> configure)
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        configure(options);
        SerializerOptions = options;
    }

    /// <summary>
    /// Advanced SDK access configuration.
    /// </summary>
    public AdvancedOptions Advanced { get; } = new();

    /// <summary>
    /// Experimental feature flags. Currently includes SurrealML support.
    /// </summary>
    public ExperimentalOptions Experimental { get; } = new();

    /// <summary>
    /// Projection rebuild and lifecycle configuration.
    /// </summary>
    public ProjectionOptions ProjectionBuild { get; }

    public StoreOptions()
    {
        ProjectionBuild = new ProjectionOptions(this);
    }

    /// <summary>
    /// Configuration for SurrealDB pre-computed/aggregate views (DEFINE TABLE ... AS SELECT ...).
    /// Views are materialized, incrementally-updating tables created during store initialization.
    /// </summary>
    public ViewOptions Views { get; } = new();

    /// <summary>
    /// Initial data seeders. Called during <see cref="DocumentStore.InitializeAsync"/>
    /// after schema creation, projections, and views are set up.
    /// Add implementations to populate seed data on first run.
    /// </summary>
    public List<IInitialData> InitialData { get; } = new();

    /// <summary>
    /// Configuration modules applied during <see cref="DocumentStore.InitializeAsync"/>.
    /// Add instances directly or register via DI with <c>ConfigureAeroDB&lt;T&gt;()</c>.
    /// </summary>
    public List<IConfigureAeroDB> Configurators { get; } = new();

    /// <summary>
    /// Async configuration modules applied during <see cref="DocumentStore.InitializeAsync"/>
    /// after sync <see cref="Configurators"/>. Add instances directly or register via DI
    /// with <c>ConfigureAeroDBAsync&lt;T&gt;()</c>.
    /// </summary>
    public List<IAsyncConfigureAeroDB> AsyncConfigurators { get; } = new();

    /// <summary>
    /// Optional <see cref="IServiceProvider"/> for DI auto-discovery of configurators.
    /// When set before <see cref="DocumentStore.InitializeAsync"/>, the store resolves
    /// <see cref="IConfigureAeroDB"/> and <see cref="IAsyncConfigureAeroDB"/> implementations
    /// from DI and applies them automatically (in addition to <see cref="Configurators"/>).
    /// </summary>
    public IServiceProvider? ServiceProvider { get; set; }

    /// <summary>Default command timeout in seconds for SurrealDB operations. Set to null for no timeout.</summary>
    public int? CommandTimeout { get; set; }

    /// <summary>Maximum batch size for unit-of-work operations during SaveChangesAsync.</summary>
    public int UpdateBatchSize { get; set; } = 500;

    /// <summary>OpenTelemetry instrumentation configuration. Null if telemetry is not configured.</summary>
    public OpenTelemetryOptions? OpenTelemetry { get; set; }

    private Action<ResiliencePipelineBuilder>? _pollyConfiguration;

    /// <summary>Configure a Polly resilience pipeline for SurrealDB client operations.</summary>
    public Action<ResiliencePipelineBuilder>? ConfigurePolly(Action<ResiliencePipelineBuilder> configure)
    {
        _pollyConfiguration = configure;
        configure?.Invoke(new ResiliencePipelineBuilder());
        return configure;
    }

    /// <summary>Mark all document types as multi-tenanted (requires tenant ID on all documents).</summary>
    public void AllDocumentsAreMultiTenanted()
    {
        Policies.ForAllDocuments(m => m.TenancyStyle = TenancyStyle.Conjoined);
    }

    /// <summary>Configure database-per-tenant multi-tenancy strategy.</summary>
    public void MultiTenantedDatabases()
    {
        TenancyStyle = TenancyStyle.DatabasePerTenant;
    }

    /// <summary>Enable soft-delete on all document types.</summary>
    public void AllDocumentsSoftDeleted()
    {
        Policies.ForAllDocuments(m => m.SoftDeleted = true);
    }

    /// <summary>Enable optimistic concurrency on all document types.</summary>
    public void AllDocumentsEnforceOptimisticConcurrency()
    {
        UseOptimisticConcurrency = true;
        Policies.ForAllDocuments(m => m.UseOptimisticConcurrency = true);
    }

    /// <summary>Explicit IReadOnlyStoreOptions.Projections implementation.</summary>
    IReadOnlyList<IProjection> IReadOnlyStoreOptions.Projections => Projections;

    /// <summary>Explicit IReadOnlyStoreOptions.Listeners implementation.</summary>
    IReadOnlyList<IDocumentSessionListener> IReadOnlyStoreOptions.Listeners => Listeners;

    /// <summary>Explicit IReadOnlyStoreOptions.ChangeListeners implementation.</summary>
    IReadOnlyList<IChangeListener> IReadOnlyStoreOptions.ChangeListeners => ChangeListeners;

    /// <summary>
    /// Multiple database endpoints for multi-host / read-replica scenarios.
    /// When configured, sessions route to appropriate endpoints based on <see cref="ReadPreference"/>.
    /// </summary>
    public List<DatabaseEndpoint> DatabaseEndpoints { get; } = new();

    /// <summary>
    /// Read preference for query sessions. Default is <see cref="ReadPreference.Primary"/>.
    /// </summary>
    public ReadPreference ReadPreference { get; set; } = ReadPreference.Primary;

    /// <summary>
    /// Add a database endpoint to the multi-host configuration.
    /// </summary>
    public StoreOptions AddDatabaseEndpoint(string endpoint, Action<DatabaseEndpoint>? configure = null)
    {
        var ep = new DatabaseEndpoint { Endpoint = endpoint };
        configure?.Invoke(ep);
        DatabaseEndpoints.Add(ep);
        return this;
    }

    public StoreOptions Connection(string endpoint, string? ns = null, string? db = null,
        string? username = null, string? password = null, string? token = null)
    {
        Endpoint = endpoint;
        Namespace = ns;
        Database = db;
        Username = username;
        Password = password;
        Token = token;
        return this;
    }
}

/// <summary>Configuration for SurrealDB schema management. Controls auto-create behavior, schema mode (<c>SCHEMAFULL</c>/<c>SCHEMALESS</c>), analyzers, views, and per-type document mappings.</summary>
public class SchemaOptions
{
    public bool AutoCreate { get; set; } = true;

    /// <summary>
    /// When true, AeroDB will call <c>DEFINE DATABASE IF NOT EXISTS</c> for each
    /// configured schema (via <see cref="DocumentMapping{T}.Schema"/>) during
    /// store initialization. Requires the connection user to have sufficient
    /// privileges. Default is false.
    /// </summary>
    public bool AutoCreateDatabases { get; set; } = false;

    /// <summary>
    /// Configuration for SurrealDB analyzers (DEFINE ANALYZER).
    /// Analyzers are created during store initialization, before indexes that reference them.
    /// </summary>
    public AnalyzerOptions Analyzers { get; } = new();

    /// <summary>
    /// Cached document mappings, keyed by entity type.
    /// </summary>
    internal Dictionary<Type, DocumentMapping> Mappings { get; } = new();

    /// <summary>
    /// Edge table mappings, used to generate RELATION table schemas during initialization.
    /// </summary>
    internal List<object> EdgeMappings { get; } = new();

    /// <summary>
    /// Auth — DEFINE ACCESS configurations (SurrealDB v3+ replaces DEFINE LOGIN).
    /// </summary>
    public List<AccessDefinition> Accesses { get; } = new();

    /// <summary>
    /// Auth — DEFINE TOKEN configurations for JWT/HS* verification.
    /// </summary>
    public List<TokenDefinition> Tokens { get; } = new();

    /// <summary>
    /// Auth — DEFINE SCOPE configurations for user signup/signin flows.
    /// </summary>
    public List<ScopeDefinition> Scopes { get; } = new();

    /// <summary>
    /// Fluent API for document-level schema configuration (indices, tenancy policy, etc.).
    /// Creates or returns a cached <see cref="DocumentMapping{T}"/> for the specified type.
    /// </summary>
    public DocumentMapping<T> For<T>()
    {
        if (!Mappings.TryGetValue(typeof(T), out var existing))
        {
            var mapping = new DocumentMapping<T>();
            Mappings[typeof(T)] = mapping;
            return mapping;
        }
        return (DocumentMapping<T>)existing;
    }

    /// <summary>
    /// Fluent API for edge table schema configuration.
    /// Configures a SurrealDB RELATION table with IN/OUT type constraints.
    /// </summary>
    /// <typeparam name="TEdge">The edge record type (extends <see cref="EdgeRecord"/>).</typeparam>
    /// <typeparam name="TIn">The source/from node record type.</typeparam>
    /// <typeparam name="TOut">The target/to node record type.</typeparam>
    public SchemaOptions Edge<TEdge, TIn, TOut>(Action<EdgeMapping<TEdge>> configure)
        where TEdge : EdgeRecord
        where TIn : SurrealDb.Net.Models.Record
        where TOut : SurrealDb.Net.Models.Record
    {
        var mapping = new EdgeMapping<TEdge>
        {
            TableName = Metadata.MetadataDispatch.GetTableName(typeof(TEdge)),
            FromTable = Metadata.MetadataDispatch.GetTableName(typeof(TIn)),
            ToTable = Metadata.MetadataDispatch.GetTableName(typeof(TOut))
        };
        configure(mapping);
        EdgeMappings.Add(mapping);
        return this;
    }

    /// <summary>
    /// Exposes the mt_events table name for raw query scenarios.
    /// </summary>
    public string EventsTableName => "mt_events";

    /// <summary>
    /// Exposes the mt_projection_progress table name for raw query scenarios.
    /// </summary>
    public string ProjectionProgressTableName => "mt_projection_progress";

    /// <summary>
    /// Returns the event store table name for the given stream type.
    /// All streams share mt_events.
    /// </summary>
    public string ForStreams<TStream>() => "mt_events";

    /// <summary>
    /// Returns the event store table name.
    /// </summary>
    public string ForEvents() => "mt_events";

    /// <summary>
    /// Returns the projection progress table name.
    /// </summary>
    public string ForEventProgression() => "mt_projection_progress";
}

/// <summary>Defines the multi-tenancy strategy. <c>Single</c> for single-tenant; <c>Conjoined</c> uses a <c>tenant_id</c> field within a single database; <c>DatabasePerTenant</c> routes each tenant to a separate database.</summary>
public enum TenancyStyle
{
    Single,
    None,
    Conjoined,
    DatabasePerTenant
}

/// <summary>Stream identity strategy for event store streams.</summary>
public enum StreamIdentity { AsGuid, AsString }

/// <summary>Configuration for AeroDB's event sourcing subsystem. Controls event storage tables, append mode (Rich/Quick), serialization mode (JSON/Binary), and event upcasting.</summary>
public class EventSourcingOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Serialization mode for event data. Default is JSON.
    /// </summary>
    public EventSerializationMode SerializationMode { get; set; } = EventSerializationMode.Json;

    /// <summary>
    /// Append mode for event store operations. Default is <see cref="EventAppendMode.Rich"/>.
    /// </summary>
    public EventAppendMode AppendMode { get; set; } = EventAppendMode.Rich;

    /// <summary>
    /// Custom database/schema name for event store tables.
    /// Default is null (uses the main database).
    /// </summary>
    public string? DatabaseSchemaName { get; set; }

    /// <summary>
    /// Custom name for the event store database/schema.
    /// Default is null (same as DatabaseSchemaName).
    /// </summary>
    public string? EventsSchemaName { get; set; }

    /// <summary>
    /// Configuration for SurrealDB native event triggers (DEFINE EVENT).
    /// These are distinct from AeroDB's Marten-style event sourcing — they fire
    /// at the database level on CREATE/UPDATE/DELETE operations.
    /// </summary>
    public EventTriggerOptions Triggers { get; } = new();

    /// <summary>
    /// Configuration for event metadata columns (CorrelationId, CausationId, Headers).
    /// </summary>
    public MetadataConfig MetadataConfig { get; } = new();

    /// <summary>
    /// Event upcasters for migrating old event types to new types during deserialization.
    /// </summary>
    public List<IEventUpcaster> Upcasters { get; } = new();

    /// <summary>
    /// Optional predicate to mask event data for GDPR/redaction purposes.
    /// When set, events matching the predicate will have their data replaced
    /// with null before storage in the database.
    /// </summary>
    public Func<IEvent, bool>? DataMaskingPredicate { get; set; }

    /// <summary>
    /// Registered event subscriptions. Each subscription processes matching events
    /// as they are appended to streams, tracked by <see cref="ISubscription.SubscriptionName"/>.
    /// </summary>
    public List<ISubscription> Subscriptions { get; } = new();

    /// <summary>
    /// Subscribe to events matching the subscription's event types.
    /// The subscription will be tracked and invoked by the async daemon.
    /// </summary>
    /// <param name="subscription">The subscription to register.</param>
    public EventSourcingOptions Subscribe(ISubscription subscription)
    {
        Subscriptions.Add(subscription ?? throw new ArgumentNullException(nameof(subscription)));
        return this;
    }

    /// <summary>
    /// Subscribe to events with additional configuration options.
    /// </summary>
    /// <param name="subscription">The subscription to register.</param>
    /// <param name="configure">Action to configure subscription options.</param>
    public EventSourcingOptions Subscribe(ISubscription subscription, Action<SubscriptionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new SubscriptionOptions();
        configure(options);
        Subscriptions.Add(subscription);
        return this;
    }

    /// <summary>
    /// Register an event upcaster for type migration.
    /// When events with <paramref name="oldEventType"/> are deserialized, the
    /// <paramref name="upcast"/> function transforms them into the new event type.
    /// </summary>
    public EventSourcingOptions Upcast<T>(string oldEventType, Func<object, T> upcast) where T : class
    {
        Upcasters.Add(new LambdaUpcaster<T>(oldEventType, upcast));
        return this;
    }

    /// <summary>
    /// Register an <see cref="IEventUpcaster"/> via its concrete type.
    /// The upcaster is instantiated via the parameterless constructor.
    /// </summary>
    /// <typeparam name="TUpcaster">The upcaster type implementing <see cref="IEventUpcaster"/> with a parameterless constructor.</typeparam>
    public void Upcast<TUpcaster>() where TUpcaster : IEventUpcaster, new()
    {
        Upcasters.Add(new TUpcaster());
    }

    /// <summary>The stream identity mode: Guid or string based stream IDs.</summary>
    public StreamIdentity StreamIdentity { get; set; } = StreamIdentity.AsGuid;

    /// <summary>Event tenancy style.</summary>
    public TenancyStyle TenancyStyle { get; set; } = TenancyStyle.Single;

    /// <summary>When true, inline projections can queue side-effect operations.</summary>
    public bool EnableSideEffectsOnInlineProjections { get; set; }

    /// <summary>When true, aggregates are tracked through an identity map to avoid re-application.</summary>
    public bool UseIdentityMapForAggregates { get; set; }

    /// <summary>When true, a unique index is created on mt_events.event_id.</summary>
    public bool EnableUniqueIndexOnEventId { get; set; }

    /// <summary>When true, a composite index (type, seq_id) is created for event lookups.</summary>
    public bool EnableEventTypeIndex { get; set; }

    /// <summary>When true, event versions and sequences use 64-bit integers.</summary>
    public bool EnableBigIntEvents { get; set; }

    /// <summary>When true, streams must be declared with a known aggregate type before use.</summary>
    public bool UseMandatoryStreamTypeDeclaration { get; set; }

    /// <summary>Custom time provider for event timestamps. Defaults to DateTimeOffset.UtcNow.</summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>Registered event types for schema awareness and LINQ querying.</summary>
    private readonly HashSet<Type> _eventTypes = [];

    /// <summary>Event type name overrides (type -> custom event type name).</summary>
    private readonly Dictionary<Type, string> _eventTypeNames = [];

    /// <summary>Registered DCB (Daemon Coordination Batches) tag types for batched event processing.</summary>
    private readonly HashSet<Type> _tagTypes = [];

    /// <summary>Gets the registered tag types.</summary>
    internal IReadOnlySet<Type> TagTypes => _tagTypes;

    /// <summary>Registered tag type registrations with value extraction.</summary>
    internal List<ITagTypeRegistration> TagRegistrations { get; } = [];

    /// <summary>Registered tag assignment rules. Key = tag type, Value = tag value extraction + predicate.</summary>
    internal List<TagAssignmentRule> TagAssignmentRules { get; } = [];

    /// <summary>Register a tag type for batched event processing (DCB parity).</summary>
    public EventSourcingOptions RegisterTagType<TTag>()
    {
        var tagType = typeof(TTag);
        if (_tagTypes.Contains(tagType))
            return this;
        _tagTypes.Add(tagType);

        // Create a simple registration — tag value = ToString()
        TagRegistrations.Add(new SimpleTagRegistration(typeof(TTag)));
        return this;
    }

    /// <summary>
    /// Assign the given tag value to events matching the predicate.
    /// Tags enable dynamic consistency boundaries for projection daemons.
    /// </summary>
    public void AssignTagWhere<TTag>(Expression<Func<IEvent, bool>> predicate)
    {
        RegisterTagType<TTag>();
        TagAssignmentRules.Add(new TagAssignmentRule(typeof(TTag), predicate.Compile()));
    }

    /// <summary>Enable advanced async projection tracking via the daemon.</summary>
    public bool EnableAdvancedAsyncTracking { get; set; }

    /// <summary>Register an event type for schema awareness.</summary>
    public EventSourcingOptions AddEventType<TEvent>() { _eventTypes.Add(typeof(TEvent)); return this; }

    /// <summary>Register an event type for schema awareness.</summary>
    public EventSourcingOptions AddEventType(Type eventType) { _eventTypes.Add(eventType); return this; }

    /// <summary>Register multiple event types for schema awareness.</summary>
    public EventSourcingOptions AddEventTypes(IEnumerable<Type> types) { foreach (var t in types) _eventTypes.Add(t); return this; }

    /// <summary>Map an event type to a custom name for storage.</summary>
    public EventSourcingOptions MapEventType<TEvent>(string eventTypeName) { _eventTypeNames[typeof(TEvent)] = eventTypeName; return this; }

    /// <summary>Map an event type to a custom name for storage.</summary>
    public EventSourcingOptions MapEventType(Type eventType, string eventTypeName) { _eventTypeNames[eventType] = eventTypeName; return this; }
}

/// <summary>Configuration for event projections. Controls which projections are registered, rebuild options (<c>RebuildOnStartup</c>/<c>RebuildProjectionNames</c>), and projection lifecycle strategies.</summary>
public class ProjectionOptions
{
    private StoreOptions _options;

    internal ProjectionOptions(StoreOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Whether to rebuild all projections during DocumentStore initialization.
    /// When true, each registered projection's RebuildAsync is called on startup.
    /// Useful for development or recovering from projection data loss.
    /// </summary>
    public bool RebuildOnStartup { get; set; }

    /// <summary>
    /// Array of projection names to rebuild on startup.
    /// When empty (default), all projections are rebuilt if <see cref="RebuildOnStartup"/> is true.
    /// When specified, only the named projections are rebuilt.
    /// Names should match IProjection EventTypes or a user-defined projection name.
    /// </summary>
    public string[] RebuildProjectionNames { get; set; } = [];

    /// <summary>
    /// Whether to ensure the projection progress state table exists.
    /// Creates mt_projection_progress if not present.
    /// </summary>
    public bool EnsureStateTable { get; set; } = true;

    /// <summary>
    /// Composite projections registered via <see cref="CompositeProjectionFor"/>.
    /// </summary>
    internal List<CompositeProjection> CompositeProjections { get; } = new();

    /// <summary>
    /// Register a composite projection that chains multiple sub-projections.
    /// </summary>
    public CompositeProjection CompositeProjectionFor(string name, Action<CompositeProjection> configure)
    {
        var composite = new CompositeProjection(name);
        configure(composite);
        CompositeProjections.Add(composite);
        return composite;
    }

    /// <summary>
    /// Register a self-aggregating snapshot projection for T with the given lifecycle.
    /// Returns the document mapping for chaining (1:1 Marten parity).
    /// </summary>
    public DocumentMapping<T> Snapshot<T>(ProjectionLifecycle lifecycle, Action<SnapshotOptions>? configure = null)
        where T : Record, new()
    {
        var options = new SnapshotOptions();
        configure?.Invoke(options);
        options.Lifecycle = lifecycle; // Explicit parameter takes precedence over configure delegate
        var projection = new SnapshotProjection<T>(options);
        _options.Projections.Add(projection);
        return _options.Schema.For<T>();
    }
}

/// <summary>Configuration for SurrealDB custom functions (<c>DEFINE FUNCTION</c>). Registers functions that run at the database level.</summary>
public class FunctionOptions
{
    internal List<SurrealFunction> Functions { get; } = new();
    public bool AutoCreateFunctions { get; set; } = true;

    public FunctionOptions Register(string name, string body, string? parameters = null)
    {
        Functions.Add(new SurrealFunction
        {
            Name = name,
            Body = body,
            Parameters = parameters
        });
        return this;
    }

    public FunctionOptions Register(string name, string body, params SurrealFunctionParameter[] parameters)
    {
        Functions.Add(new SurrealFunction
        {
            Name = name,
            Body = body,
            ParametersTyped = parameters.ToList()
        });
        return this;
    }
}

/// <summary>
/// SurrealDB DEFINE ACCESS configuration (replaces DEFINE LOGIN in v3+).
/// </summary>
public class AccessDefinition
{
    /// <summary>Access method name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Access type: RECORD (default), JWT, or TOKEN.</summary>
    public string Type { get; set; } = "RECORD";

    /// <summary>Optional SIGNUP SurrealQL block.</summary>
    public string? SignupQuery { get; set; }

    /// <summary>Optional SIGNIN SurrealQL block.</summary>
    public string? SigninQuery { get; set; }

    /// <summary>Token duration (e.g., "24h", "7d").</summary>
    public string? Duration { get; set; } = "24h";
}

/// <summary>
/// SurrealDB DEFINE TOKEN configuration for JWT/HS* token verification.
/// </summary>
public class TokenDefinition
{
    /// <summary>Token name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Algorithm type: HS256 (default), HS512, RS256, ES256, etc.</summary>
    public string Type { get; set; } = "HS256";

    /// <summary>Secret or public key value.</summary>
    public string Value { get; set; } = "";
}

/// <summary>
/// Internal registration of a tag type that extracts the tag value via ToString().
/// </summary>
internal sealed class SimpleTagRegistration : ITagTypeRegistration
{
    public Type TagType { get; }
    public Type? AggregateType { get; }

    public SimpleTagRegistration(Type tagType, Type? aggregateType = null)
    {
        TagType = tagType;
        AggregateType = aggregateType;
    }

    public string ExtractValue(object tag) => tag?.ToString() ?? "";
}

/// <summary>
/// A rule that assigns a tag value to events matching a predicate.
/// </summary>
internal sealed class TagAssignmentRule(Type tagType, Func<IEvent, bool> predicate)
{
    public Type TagType { get; } = tagType;
    public Func<IEvent, bool> Predicate { get; } = predicate;
}

/// <summary>
/// SurrealDB DEFINE SCOPE configuration for user signup/signin flows.
/// </summary>
public class ScopeDefinition
{
    /// <summary>Scope name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Session duration (e.g., "24h").</summary>
    public string SessionDuration { get; set; } = "24h";

    /// <summary>Optional SIGNUP SurrealQL block.</summary>
    public string? SignupQuery { get; set; }

    /// <summary>Optional SIGNIN SurrealQL block.</summary>
    public string? SigninQuery { get; set; }
}
