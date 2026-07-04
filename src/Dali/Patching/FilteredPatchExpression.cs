using System.Linq.Expressions;
using System.Reflection;
using Dali.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dali;

/// <summary>
/// Patch expression that applies to ALL documents matching a filter expression
/// rather than a single identified document.
/// </summary>
internal class FilteredPatchExpression<T> : IPatchExpression<T>, IDeferredPatch where T : class
{
    private readonly IDocumentSession _session;
    private readonly Expression<Func<T, bool>> _filter;
    private readonly List<SetOperation> _operations = new();
    private readonly ILogger<FilteredPatchExpression<T>> _logger;

    public FilteredPatchExpression(IDocumentSession session, Expression<Func<T, bool>> filter)
    {
        _session = session;
        _filter = filter;
        _logger = ((InternalSessionBase)session).StoreOptions.LoggerFactory
            ?.CreateLogger<FilteredPatchExpression<T>>()
            ?? NullLogger<FilteredPatchExpression<T>>.Instance;

        // Auto-register on the session for execution during SaveChangesAsync
        if (session is DocumentSession ds)
            ds._queuedPatches.Add(this);
    }

    public IPatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> property, TValue value)
    {
        _operations.Add(new SetOperation(GetMember(property).Name, value, OperationKind.Set));
        return this;
    }

    public IPatchExpression<T> Increment(Expression<Func<T, int>> property, int amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment)); return this; }
    public IPatchExpression<T> Increment(Expression<Func<T, long>> property, long amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment)); return this; }
    public IPatchExpression<T> Increment(Expression<Func<T, double>> property, double amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment)); return this; }
    public IPatchExpression<T> Increment(Expression<Func<T, float>> property, float amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment)); return this; }
    public IPatchExpression<T> Increment(Expression<Func<T, decimal>> property, decimal amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment)); return this; }
    public IPatchExpression<T> Append<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.Append)); return this; }
    public IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.AppendIfNotExists)); return this; }
    public IPatchExpression<T> Insert<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element, int? index = null)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.Insert, insertIndex: index)); return this; }
    public IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element, int? index = null)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.InsertIfNotExists, insertIndex: index)); return this; }
    public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.Remove)); return this; }
    public IPatchExpression<T> Duplicate<TElement>(Expression<Func<T, TElement>> source, params Expression<Func<T, TElement>>[] destinations)
    {
        var src = GetMember(source).Name;
        foreach (var d in destinations)
            _operations.Add(new SetOperation(GetMember(d).Name, null, OperationKind.Duplicate, targetField: src));
        return this;
    }
    public IPatchExpression<T> Rename(string oldName, Expression<Func<T, object?>> target)
    { _operations.Add(new SetOperation(GetMember(target).Name, null, OperationKind.Rename, oldName: oldName)); return this; }
    public IPatchExpression<T> Delete<TValue>(Expression<Func<T, TValue>> property)
    { _operations.Add(new SetOperation(GetMember(property).Name, null, OperationKind.Delete)); return this; }

    public IPatchExpression<T> SetAll<TValue>(TValue value)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.PropertyType == typeof(TValue));
        foreach (var prop in props)
            _operations.Add(new SetOperation(prop.Name, value, OperationKind.Set));
        return this;
    }

    async Task IDeferredPatch.ExecuteAsync(IDocumentSession session, CancellationToken ct)
    {
        if (_operations.Count == 0) return;

        var table = MetadataDispatch.GetTableName(typeof(T));
        var surrealdbSession = ((InternalSessionBase)session).Session;

        // Convert the filter expression to a WHERE clause
        var whereClause = CompileFilter(_filter);

        var renameOps = _operations.Where(o => o.Kind == OperationKind.Rename).ToList();
        var updateOps = _operations.Where(o => o.Kind != OperationKind.Rename).ToList();

        if (updateOps.Count > 0)
        {
            var sets = updateOps.Select(o => o.ToSurrealQL()).ToList();
            var surql = $"UPDATE {table} SET {string.Join(", ", sets)} WHERE {whereClause};";
            _logger.LogDebug("Applying filtered patch: {SurrealQL}", surql);
            await surrealdbSession.RawQuery(surql, null, ct).ConfigureAwait(false);
        }

        // Rename operations require ALTER TABLE (not filterable by WHERE)
        foreach (var op in renameOps)
        {
            var surql = $"ALTER TABLE {table} RENAME COLUMN `{op.OldName}` TO `{op.FieldName}`;";
            _logger.LogDebug("Applying rename: {SurrealQL}", surql);
            await surrealdbSession.RawQuery(surql, null, ct).ConfigureAwait(false);
        }

        _operations.Clear();
    }

    /// <summary>
    /// Backward-compatible public method that immediately applies the patch.
    /// Delegates to the deferred execution path.
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        await ((IDeferredPatch)this).ExecuteAsync(_session, ct).ConfigureAwait(false);
    }

    private static string CompileFilter(Expression<Func<T, bool>> filter)
    {
        // Use the SurrealExpressionVisitor's internal TranslateCondition helper
        return SurrealExpressionVisitor.TranslateCondition(filter.Body);
    }

    private static MemberInfo GetMember<TValue>(Expression<Func<T, TValue>> property)
    {
        return property.Body switch
        {
            MemberExpression me => me.Member,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Operand: MemberExpression me } => me.Member,
            _ => throw new ArgumentException("Expression must be a property access expression.", nameof(property))
        };
    }
}
