using System.Linq.Expressions;
using AeroDB.Metadata;

namespace AeroDB;

/// <summary>
/// Fluent builder for view SELECT columns. Supports aggregates (count/sum/min/max/average),
/// column references, graph traversal entries, and raw SurrealQL expressions.
/// Returned from <c>ViewDefinition&lt;T&gt;.Select(cols => ...)</c>.
/// </summary>
public class ViewSelectBuilder<T> where T : class
{
    private readonly List<(string Expression, string? Alias)> _columns = new();
    private bool _pendingAlias;

    /// <summary>Adds <c>count()</c> aggregate.</summary>
    public ViewSelectBuilder<T> Count()
    {
        _columns.Add(("count()", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds <c>math::sum(field)</c> aggregate.</summary>
    public ViewSelectBuilder<T> Sum(Expression<Func<T, object?>> field)
    {
        var name = GetMemberName(field.Body);
        _columns.Add(($"math::sum({name})", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds <c>math::min(field)</c> aggregate.</summary>
    public ViewSelectBuilder<T> Min(Expression<Func<T, object?>> field)
    {
        var name = GetMemberName(field.Body);
        _columns.Add(($"math::min({name})", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds <c>math::max(field)</c> aggregate.</summary>
    public ViewSelectBuilder<T> Max(Expression<Func<T, object?>> field)
    {
        var name = GetMemberName(field.Body);
        _columns.Add(($"math::max({name})", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds <c>math::mean(field)</c> aggregate.</summary>
    public ViewSelectBuilder<T> Average(Expression<Func<T, object?>> field)
    {
        var name = GetMemberName(field.Body);
        _columns.Add(($"math::mean({name})", null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Adds a bare column reference (no aggregation).</summary>
    public ViewSelectBuilder<T> Column(Expression<Func<T, object?>> field)
    {
        var name = GetMemberName(field.Body);
        _columns.Add((name, null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>
    /// Sets the alias for the most recently added expression.
    /// Throws <see cref="InvalidOperationException"/> if no pending expression exists.
    /// </summary>
    public ViewSelectBuilder<T> As(string alias)
    {
        if (!_pendingAlias || _columns.Count == 0)
            throw new InvalidOperationException(
                "No pending expression to alias. Call .As() after .Count(), .Sum(), .Column(), etc.");

        var last = _columns[^1];
        _columns[^1] = (last.Expression, alias);
        _pendingAlias = false;
        return this;
    }

    /// <summary>
    /// Starts a graph traversal expression chain.
    /// Returns a <see cref="GraphEntry{T}"/> which provides .Out() and .In() steps
    /// leading to a typed <see cref="ViewGraphSelectBuilder{TSource,TTarget}"/>.
    /// </summary>
    public GraphEntry<T> Graph()
    {
        return new GraphEntry<T>(this);
    }

    /// <summary>
    /// Escape hatch: adds a raw SurrealQL expression to the column list.
    /// Typically followed by <see cref="As"/> to attach an alias.
    /// </summary>
    public ViewSelectBuilder<T> Raw(string surqlExpression)
    {
        _columns.Add((surqlExpression, null));
        _pendingAlias = true;
        return this;
    }

    /// <summary>Builds the final comma-separated SELECT column string.</summary>
    internal string Build()
    {
        if (_columns.Count == 0) return "*";
        return string.Join(", ", _columns.Select(c =>
            c.Alias is not null ? $"{c.Expression} AS {c.Alias}" : c.Expression));
    }

    /// <summary>Records a graph column expression (called by ViewGraphSelectBuilder).</summary>
    internal void AddColumn(string expression)
    {
        _columns.Add((expression, null));
        _pendingAlias = true;
    }

    internal static string GetMemberName(Expression body)
    {
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.TypeAs } unary)
            body = unary.Operand;
        if (body is MemberExpression m)
            return m.Member.Name;
        throw new ArgumentException("Selector must be a simple member expression (e.g., x => x.Property).");
    }
}

/// <summary>
/// Entry point for a graph traversal chain within a view SELECT column.
/// Provides .Out() and .In() steps that each produce a typed <see cref="ViewGraphSelectBuilder{TSource,TTarget}"/>.
/// </summary>
public class GraphEntry<TSource> where TSource : class
{
    private readonly ViewSelectBuilder<TSource> _parent;

    internal GraphEntry(ViewSelectBuilder<TSource> parent)
    {
        _parent = parent;
    }

    /// <summary>Forward traversal with a named edge: -&gt;edge_type-&gt;target_table.</summary>
    public ViewGraphSelectBuilder<TSource, TTarget> Out<TTarget>(string edgeType) where TTarget : class
    {
        var table = MetadataDispatch.GetTableName(typeof(TTarget));
        return new ViewGraphSelectBuilder<TSource, TTarget>(_parent, $"->{edgeType}->{table}");
    }

    /// <summary>Backward traversal with a named edge: &lt;-edge_type&lt;-target_table.</summary>
    public ViewGraphSelectBuilder<TSource, TTarget> In<TTarget>(string edgeType) where TTarget : class
    {
        var table = MetadataDispatch.GetTableName(typeof(TTarget));
        return new ViewGraphSelectBuilder<TSource, TTarget>(_parent, $"<-{edgeType}<-{table}");
    }

    /// <summary>Forward traversal with a wildcard edge: -&gt;?-&gt;target_table.</summary>
    public ViewGraphSelectBuilder<TSource, TTarget> Out<TTarget>() where TTarget : class
    {
        var table = MetadataDispatch.GetTableName(typeof(TTarget));
        return new ViewGraphSelectBuilder<TSource, TTarget>(_parent, $"->?->{table}");
    }

    /// <summary>Backward traversal with a wildcard edge: &lt;-?&lt;-target_table.</summary>
    public ViewGraphSelectBuilder<TSource, TTarget> In<TTarget>() where TTarget : class
    {
        var table = MetadataDispatch.GetTableName(typeof(TTarget));
        return new ViewGraphSelectBuilder<TSource, TTarget>(_parent, $"<-?<-{table}");
    }
}

/// <summary>
/// Typed graph traversal builder within a view SELECT column.
/// <typeparamref name="TSource"/> is the view's source entity type.
/// <typeparamref name="TTarget"/> is the current traversal target type (used by <c>Select</c>).
/// Call <c>Select</c> to close the chain and return to the parent <see cref="ViewSelectBuilder{TSource}"/>.
/// </summary>
public class ViewGraphSelectBuilder<TSource, TTarget>
    where TSource : class
    where TTarget : class
{
    private readonly ViewSelectBuilder<TSource> _parent;
    private readonly string _path;
    private bool _closed;

    internal ViewGraphSelectBuilder(ViewSelectBuilder<TSource> parent, string path)
    {
        _parent = parent;
        _path = path;
    }

    /// <summary>Continue forward traversal with a named edge.</summary>
    public ViewGraphSelectBuilder<TSource, TNewTarget> Out<TNewTarget>(string edgeType) where TNewTarget : class
    {
        GuardOpen();
        var table = MetadataDispatch.GetTableName(typeof(TNewTarget));
        return new ViewGraphSelectBuilder<TSource, TNewTarget>(_parent, $"{_path}->{edgeType}->{table}");
    }

    /// <summary>Continue backward traversal with a named edge.</summary>
    public ViewGraphSelectBuilder<TSource, TNewTarget> In<TNewTarget>(string edgeType) where TNewTarget : class
    {
        GuardOpen();
        var table = MetadataDispatch.GetTableName(typeof(TNewTarget));
        return new ViewGraphSelectBuilder<TSource, TNewTarget>(_parent, $"{_path}<-{edgeType}<-{table}");
    }

    /// <summary>Continue forward traversal with a wildcard edge.</summary>
    public ViewGraphSelectBuilder<TSource, TNewTarget> Out<TNewTarget>() where TNewTarget : class
    {
        GuardOpen();
        var table = MetadataDispatch.GetTableName(typeof(TNewTarget));
        return new ViewGraphSelectBuilder<TSource, TNewTarget>(_parent, $"{_path}->?->{table}");
    }

    /// <summary>Continue backward traversal with a wildcard edge.</summary>
    public ViewGraphSelectBuilder<TSource, TNewTarget> In<TNewTarget>() where TNewTarget : class
    {
        GuardOpen();
        var table = MetadataDispatch.GetTableName(typeof(TNewTarget));
        return new ViewGraphSelectBuilder<TSource, TNewTarget>(_parent, $"{_path}<-?<-{table}");
    }

    /// <summary>
    /// Closes the graph traversal chain by selecting a field on the current target,
    /// and returns to the parent <see cref="ViewSelectBuilder{TSource}"/>.
    /// </summary>
    /// <param name="field">Expression selecting a property on <typeparamref name="TTarget"/>.</param>
    public ViewSelectBuilder<TSource> Select(Expression<Func<TTarget, object?>> field)
    {
        GuardOpen();
        var fieldName = ViewSelectBuilder<TSource>.GetMemberName(field.Body);
        _parent.AddColumn($"{_path}.{fieldName}");
        _closed = true;
        return _parent;
    }

    /// <summary>
    /// Closes the graph traversal chain by selecting a named field,
    /// and returns to the parent <see cref="ViewSelectBuilder{TSource}"/>.
    /// </summary>
    public ViewSelectBuilder<TSource> Select(string fieldName)
    {
        GuardOpen();
        _parent.AddColumn($"{_path}.{fieldName}");
        _closed = true;
        return _parent;
    }

    private void GuardOpen()
    {
        if (_closed)
            throw new InvalidOperationException(
                "Graph traversal is closed. Call .Graph() again to start a new chain.");
    }
}
