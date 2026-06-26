using Microsoft.Extensions.Logging;
using SurrealDb.Net;

namespace Dali;

/// <summary>The main configuration object for a Dali document store. Configures database connection settings, schema generation mode, projections, event sourcing, multi-tenancy, and all other store behaviors.</summary>
public class StoreOptions
{
    public string Endpoint { get; set; } = "http://localhost:8000";
    public string? Namespace { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Token { get; set; }

    public SchemaOptions Schema { get; } = new();
    public TenancyStyle TenancyStyle { get; set; }
    public string? DefaultTenantId { get; set; }
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
    /// When true, documents with an <see cref="IVersioned"/> version field or
    /// a property decorated with <see cref="VersionAttribute"/> are protected
    /// against lost updates. Before saving a modified document, Dali checks that
    /// the current database version matches the version captured when the document
    /// was loaded or stored. If the versions differ, a <see cref="ConcurrencyException"/>
    /// is thrown.
    /// </summary>
    public bool UseOptimisticConcurrency { get; set; }

    public Func<ISurrealDbClient>? ClientFactory { get; set; }

    /// <summary>
    /// Logger factory for creating typed loggers throughout the Dali stack.
    /// If null, <c>NullLogger{T}</c> is used everywhere (no-op).
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// Minimum log level for Dali library log messages.
    /// </summary>
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Registered projections (inline and async).
    /// </summary>
    public List<IProjection> Projections { get; } = new();

    /// <summary>
    /// EF Core projections that write through a DbContext.
    /// </summary>
    public List<IProjection> EfCoreProjections { get; } = new();

    /// <summary>
    /// When true (default), queries automatically filter out soft-deleted documents
    /// (those where <c>Deleted = true</c>). Set to false for admin views that need
    /// to include soft-deleted records.
    /// Soft-delete behavior always applies to entities implementing <see cref="ISoftDeleted"/>
    /// regardless of this setting — only the query auto-filter is affected.
    /// </summary>
    public bool SoftDeleteEnabled { get; set; } = true;

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
    public ProjectionOptions ProjectionBuild { get; } = new();

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
    /// Add instances directly or register via DI with <c>ConfigureDali&lt;T&gt;()</c>.
    /// </summary>
    public List<IConfigureDali> Configurators { get; } = new();

    /// <summary>
    /// Async configuration modules applied during <see cref="DocumentStore.InitializeAsync"/>
    /// after sync <see cref="Configurators"/>. Add instances directly or register via DI
    /// with <c>ConfigureDaliAsync&lt;T&gt;()</c>.
    /// </summary>
    public List<IAsyncConfigureDali> AsyncConfigurators { get; } = new();

    /// <summary>
    /// Optional <see cref="IServiceProvider"/> for DI auto-discovery of configurators.
    /// When set before <see cref="DocumentStore.InitializeAsync"/>, the store resolves
    /// <see cref="IConfigureDali"/> and <see cref="IAsyncConfigureDali"/> implementations
    /// from DI and applies them automatically (in addition to <see cref="Configurators"/>).
    /// </summary>
    public IServiceProvider? ServiceProvider { get; set; }

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
    /// When true, Dali will call <c>DEFINE DATABASE IF NOT EXISTS</c> for each
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
    /// Fluent API for document-level schema configuration (indices, tenancy policy, etc.).
    /// Creates or returns a cached <see cref="DocumentMapping{T}"/> for the specified type.
    /// </summary>
    public DocumentMapping<T> For<T>() where T : class
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

/// <summary>Defines the multi-tenancy strategy. <c>Conjoined</c> uses a <c>tenant_id</c> field within a single database; <c>DatabasePerTenant</c> routes each tenant to a separate database.</summary>
public enum TenancyStyle
{
    None,
    Conjoined,
    DatabasePerTenant
}

/// <summary>Configuration for Dali's event sourcing subsystem. Controls event storage tables, append mode (Rich/Quick), serialization mode (JSON/Binary), and event upcasting.</summary>
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
    /// These are distinct from Dali's Marten-style event sourcing — they fire
    /// at the database level on CREATE/UPDATE/DELETE operations.
    /// </summary>
    public EventTriggerOptions Triggers { get; } = new();

    /// <summary>
    /// Event upcasters for migrating old event types to new types during deserialization.
    /// </summary>
    public List<IEventUpcaster> Upcasters { get; } = new();

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
}

/// <summary>Configuration for event projections. Controls which projections are registered, rebuild options (<c>RebuildOnStartup</c>/<c>RebuildProjectionNames</c>), and projection lifecycle strategies.</summary>
public class ProjectionOptions
{
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
