using Microsoft.Extensions.Logging;
using SurrealDb.Net;

namespace Dali;

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
    /// Configuration modules applied during <see cref="DocumentStore.InitializeAsync"/>.
    /// Add instances directly or register via DI with <c>ConfigureDali&lt;T&gt;()</c>.
    /// </summary>
    public List<IConfigureDali> Configurators { get; } = new();

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
    public DocumentMapping<T> For<T>() where T : SurrealDb.Net.Models.IRecord
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
}

public enum TenancyStyle
{
    None,
    Conjoined,
    DatabasePerTenant
}

public class EventSourcingOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Configuration for SurrealDB native event triggers (DEFINE EVENT).
    /// These are distinct from Dali's Marten-style event sourcing — they fire
    /// at the database level on CREATE/UPDATE/DELETE operations.
    /// </summary>
    public EventTriggerOptions Triggers { get; } = new();
}

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
