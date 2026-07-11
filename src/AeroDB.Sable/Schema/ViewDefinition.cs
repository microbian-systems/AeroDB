using System.Linq.Expressions;
using SurrealDb.Net;

namespace AeroDB.Sable;

/// <summary>
/// Non-generic base for view definitions. Used by <see cref="ViewOptions.ViewRegistration"/>
/// to enable schema-aware routing of views to per-schema databases.
/// </summary>
public abstract class ViewDefinition
{
    /// <summary>The SurrealDB table name for the view.</summary>
    public string ViewName { get; }

    /// <summary>
    /// Optional schema (database) name for multi-database setups.
    /// When non-null, the view is created in the specified schema database.
    /// When null, the view is created in the default database.
    /// </summary>
    public string? SchemaName { get; protected set; }

    protected ViewDefinition(string viewName)
    {
        ArgumentNullException.ThrowIfNull(viewName);
        ViewName = viewName;
    }

    /// <summary>
    /// Builds the complete SurrealQL SELECT clause for the view definition.
    /// </summary>
    internal abstract string BuildSelectSurql();
}

/// <summary>
/// Fluent builder for SurrealDB pre-computed/aggregate views (DEFINE TABLE ... AS SELECT ...).
/// Views are materialized, incrementally-updating tables backed by a SELECT query.
/// </summary>
/// <typeparam name="T">The entity type representing the view's result shape.</typeparam>
public class ViewDefinition<T> : ViewDefinition where T : class
{
    public string? FromTable { get; private set; }
    public string? SelectColumns { get; private set; }
    public string? WhereClause { get; private set; }
    public string? GroupByColumns { get; private set; }
    public bool IsDrop { get; private set; }

    private string? _rawSelectSurql;

    internal ViewDefinition(string viewName) : base(viewName)
    {
    }

    /// <summary>
    /// Assigns this view to a specific schema (database).
    /// When set, the view is created in the specified schema database
    /// instead of the default database.
    /// </summary>
    /// <param name="schemaName">The schema/database name.</param>
    public ViewDefinition<T> Schema(string schemaName)
    {
        ArgumentNullException.ThrowIfNull(schemaName);
        SchemaName = schemaName;
        return this;
    }

    /// <summary>
    /// Bypasses all builder methods and uses the provided SurrealQL SELECT verbatim.
    /// Mutually exclusive with <see cref="From{TFrom}"/>, <see cref="Where"/>,
    /// <see cref="GroupBy{TKey}"/>, <see cref="WithSelect"/>, and <see cref="Select"/>.
    /// </summary>
    /// <param name="selectSurql">The complete SurrealQL SELECT statement (e.g., "SELECT count() AS n FROM my_table").</param>
    public ViewDefinition<T> RawView(string selectSurql)
    {
        ArgumentNullException.ThrowIfNull(selectSurql);
        _rawSelectSurql = selectSurql;
        return this;
    }

    private void GuardNotRaw()
    {
        if (_rawSelectSurql is not null)
            throw new InvalidOperationException(
                "Cannot call this method after .RawView(). .RawView() bypasses the fluent builder entirely.");
    }

    /// <summary>
    /// Sets the source table for the view SELECT.
    /// </summary>
    public ViewDefinition<T> From<TFrom>() where TFrom : class
    {
        GuardNotRaw();
        FromTable = Metadata.MetadataDispatch.GetTableName(typeof(TFrom));
        return this;
    }

    /// <summary>
    /// Sets the source table for the view SELECT using the provided table name directly.
    /// Useful when the source type is not registered in the metadata system.
    /// </summary>
    public ViewDefinition<T> From(string tableName)
    {
        GuardNotRaw();
        ArgumentNullException.ThrowIfNull(tableName);
        FromTable = tableName;
        return this;
    }

    /// <summary>
    /// Adds a WHERE clause to the view, filtering the source data.
    /// The predicate is translated to SurrealQL using the same expression visitor
    /// that powers LINQ queries. Values are inlined since view definitions are schema.
    /// </summary>
    public ViewDefinition<T> Where(Expression<Func<T, bool>> predicate)
    {
        GuardNotRaw();
        ArgumentNullException.ThrowIfNull(predicate);
        // Use the inline overload (no parameterization) since view WHERE clauses
        // are schema definitions, not user-supplied query filters.
        WhereClause = SurrealExpressionVisitor.TranslateCondition(predicate.Body);
        return this;
    }

    /// <summary>
    /// Adds a GROUP BY clause to the view, grouping results by the specified columns.
    /// </summary>
    public ViewDefinition<T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        GuardNotRaw();
        ArgumentNullException.ThrowIfNull(keySelector);
        var columns = SurrealExpressionVisitor.ExtractGroupByColumns(keySelector);
        GroupByColumns = string.Join(", ", columns);
        return this;
    }

    /// <summary>
    /// Specifies a custom SELECT column list for the view.
    /// Use this for aggregate functions, graph traversal expressions, and custom projections.
    /// </summary>
    /// <param name="columns">
    /// The SurrealQL column expressions, e.g. "count() AS num, math::mean(rating) AS avg, ->product.id AS product_id".
    /// </param>
    public ViewDefinition<T> WithSelect(string columns)
    {
        GuardNotRaw();
        ArgumentNullException.ThrowIfNull(columns);
        SelectColumns = columns;
        return this;
    }

    /// <summary>
    /// Defines the SELECT columns using a type-safe fluent builder.
    /// Supports aggregates, column references, graph traversal, and raw SurrealQL expressions.
    /// Preferred over <see cref="WithSelect"/> for structured projections.
    /// </summary>
    /// <example>
    /// <code>
    /// view.Select(cols => cols
    ///     .Count().As("total")
    ///     .Average(r => r.Rating).As("avg")
    ///     .Column(r => r.Title)
    ///     .Graph().Out&lt;Author&gt;("wrote").Select(a => a.Name).As("author")
    /// );
    /// </code>
    /// </example>
    public ViewDefinition<T> Select(Func<ViewSelectBuilder<T>, ViewSelectBuilder<T>> build)
    {
        GuardNotRaw();
        ArgumentNullException.ThrowIfNull(build);
        var builder = new ViewSelectBuilder<T>();
        build(builder);
        SelectColumns = builder.Build();
        return this;
    }

    /// <summary>
    /// Marks this view as a DROP table (DEFINE TABLE ... DROP).
    /// Records are automatically deleted after event triggers fire.
    /// Mutually exclusive with SelectColumns.
    /// </summary>
    public ViewDefinition<T> Drop()
    {
        IsDrop = true;
        _rawSelectSurql = null;
        SelectColumns = null;
        WhereClause = null;
        GroupByColumns = null;
        return this;
    }

    /// <summary>
    /// Builds the complete SurrealQL SELECT clause for the view definition.
    /// When <see cref="RawView"/> was used, returns the verbatim SurrealQL.
    /// Otherwise builds from the fluent builder state with backtick-quoted tables.
    /// </summary>
    internal override string BuildSelectSurql()
    {
        if (IsDrop)
            throw new InvalidOperationException("DROP views do not have a SELECT clause.");
        if (_rawSelectSurql is not null)
            return _rawSelectSurql;
        if (FromTable is null)
            throw new InvalidOperationException(".From<TFrom>() or .From(string) must be called before building the SELECT.");

        var cols = SelectColumns ?? "*";
        var sql = $"SELECT {cols} FROM `{FromTable}`";
        if (WhereClause is not null)
            sql += $" WHERE {WhereClause}";
        if (GroupByColumns is not null)
            sql += $" GROUP BY {GroupByColumns}";
        return sql;
    }
}

/// <summary>
/// Configuration root for SurrealDB pre-computed/aggregate views.
/// Views are created during DocumentStore initialization.
/// </summary>
public class ViewOptions
{
    /// <summary>
    /// A registered view definition and its execution delegate.
    /// Carries schema routing information for per-database view creation.
    /// </summary>
    internal class ViewRegistration
    {
        /// <summary>The view definition instance (non-generic base for schema routing).</summary>
        public ViewDefinition Definition { get; init; } = null!;

        /// <summary>
        /// Executes the view creation against the given session, using the provided
        /// SchemaManager (shared from the DocumentStore initialization context).
        /// </summary>
        public Func<ISurrealDbSession, SchemaManager, CancellationToken, Task> ExecuteAsync { get; init; } = null!;
    }

    internal List<ViewRegistration> Configurations { get; } = new();

    /// <summary>
    /// Starts defining a new view with the specified name.
    /// </summary>
    /// <typeparam name="T">The entity type representing the view's result shape.</typeparam>
    /// <param name="viewName">The SurrealDB table name for the view.</param>
    public ViewDefinition<T> For<T>(string viewName) where T : class
    {
        var def = new ViewDefinition<T>(viewName);
        var reg = new ViewRegistration
        {
            Definition = def,
            ExecuteAsync = async (session, mgr, ct) =>
            {
                await mgr.EnsureViewAsync(session, def, ct).ConfigureAwait(false);
            }
        };
        Configurations.Add(reg);
        return def;
    }
}
