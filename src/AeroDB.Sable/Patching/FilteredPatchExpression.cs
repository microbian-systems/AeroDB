using System.Linq.Expressions;
using System.Reflection;
using AeroDB.Sable.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SurrealDb.Net;

namespace AeroDB.Sable;

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
    private readonly EnumStorage _enumStorage;
    private PatchContext? _patchContext;

    public FilteredPatchExpression(IDocumentSession session, Expression<Func<T, bool>> filter)
    {
        _session = session;
        _filter = filter;
        var storeOptions = ((InternalSessionBase)session).StoreOptions;
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), storeOptions.Schema))
            throw new SableEncryptedOperationNotSupportedException(typeof(T), "filtered patch");
        _logger = storeOptions.LoggerFactory
            ?.CreateLogger<FilteredPatchExpression<T>>()
            ?? NullLogger<FilteredPatchExpression<T>>.Instance;
        _enumStorage = storeOptions.EnumStorage;

        // Auto-register on the session for execution during SaveChangesAsync
        if (session is DocumentSession ds)
            ds._queuedPatches.Add(this);
    }

    public IPatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> property, TValue value)
    {
        _operations.Add(new SetOperation(GetMember(property).Name, value, OperationKind.Set, enumStorage: _enumStorage));
        return this;
    }

    public IPatchExpression<T> Increment(Expression<Func<T, int>> property, int amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> Increment(Expression<Func<T, long>> property, long amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> Increment(Expression<Func<T, double>> property, double amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> Increment(Expression<Func<T, float>> property, float amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> Increment(Expression<Func<T, decimal>> property, decimal amount = 1)
    { _operations.Add(new SetOperation(GetMember(property).Name, amount, OperationKind.Increment, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> Append<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.Append, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.AppendIfNotExists, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> Insert<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element, int? index = null)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.Insert, insertIndex: index, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element, int? index = null)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.InsertIfNotExists, insertIndex: index, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element)
    { _operations.Add(new SetOperation(GetMember(property).Name, element, OperationKind.Remove, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> Duplicate<TElement>(Expression<Func<T, TElement>> source, params Expression<Func<T, TElement>>[] destinations)
    {
        var src = GetMember(source).Name;
        foreach (var d in destinations)
            _operations.Add(new SetOperation(GetMember(d).Name, null, OperationKind.Duplicate, targetField: src, enumStorage: _enumStorage));
        return this;
    }
    public IPatchExpression<T> Rename(string oldName, Expression<Func<T, object?>> target)
    { _operations.Add(new SetOperation(GetMember(target).Name, null, OperationKind.Rename, oldName: oldName, enumStorage: _enumStorage)); return this; }
    public IPatchExpression<T> Delete<TValue>(Expression<Func<T, TValue>> property)
    { _operations.Add(new SetOperation(GetMember(property).Name, null, OperationKind.Delete, enumStorage: _enumStorage)); return this; }

    public IPatchExpression<T> SetAll<TValue>(TValue value)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.PropertyType == typeof(TValue));
        foreach (var prop in props)
            _operations.Add(new SetOperation(prop.Name, value, OperationKind.Set, enumStorage: _enumStorage));
        return this;
    }

    public IPatchExpression<T> WithReason(string reason)
    {
        _patchContext ??= new PatchContext();
        _patchContext.Reason = reason;
        return this;
    }

    internal PatchContext? PatchContext => _patchContext;

    async Task IDeferredPatch.ExecuteAsync(
        IDocumentSession session,
        ISurrealDbSession executionSession,
        CancellationToken ct)
    {
        if (_operations.Count == 0) return;

        var internalSession = (InternalSessionBase)session;
        var schema = internalSession.StoreOptions.Schema;
        var table = MetadataDispatch.GetTableName(typeof(T), schema);
        var surrealdbSession = executionSession;

        // Convert the filter expression to a WHERE clause
        var whereClause = SurrealExpressionVisitor.TranslateCondition(_filter.Body, schema);

        var renameOps = _operations.Where(o => o.Kind == OperationKind.Rename).ToList();
        var updateOps = _operations.Where(o => o.Kind != OperationKind.Rename).ToList();

        if (updateOps.Count > 0)
        {
            var sets = updateOps.Select(o => MapOperation(o, schema).ToSurrealQL()).ToList();
            var surql = $"UPDATE {table} SET {string.Join(", ", sets)} WHERE {whereClause};";
            _logger.LogDebug("Applying filtered patch: {SurrealQL}", surql);
            await surrealdbSession.RawQuery(surql, null, ct).ConfigureAwait(false);
        }

        // Rename operations require ALTER TABLE (not filterable by WHERE)
        foreach (var op in renameOps)
        {
            var mapped = MapOperation(op, schema);
            var surql = $"ALTER TABLE {table} RENAME COLUMN `{mapped.OldName}` TO `{mapped.FieldName}`;";
            _logger.LogDebug("Applying rename: {SurrealQL}", surql);
            await surrealdbSession.RawQuery(surql, null, ct).ConfigureAwait(false);
        }

        // Expose patch context to listeners, if available
        if (_patchContext is not null && session is DocumentSession ds)
        {
            foreach (var listener in ds.Listeners)
            {
                // Patch context is available via the patch expression property.
                // Future PatchPipeline will dispatch context-aware notifications here.
            }
        }

        _operations.Clear();
    }

    /// <summary>
    /// Backward-compatible public method that immediately applies the patch.
    /// Delegates to the deferred execution path.
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        var executionSession = _session is DocumentSession documentSession
            ? await documentSession.GetWriteSessionAsync(typeof(T), ct).ConfigureAwait(false)
            : ((InternalSessionBase)_session).Session;
        await ((IDeferredPatch)this)
            .ExecuteAsync(_session, executionSession, ct)
            .ConfigureAwait(false);
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

    private static SetOperation MapOperation(SetOperation op, SchemaOptions schema)
        => new(
            MetadataDispatch.GetFieldName(typeof(T), op.FieldName, schema),
            op.Value,
            op.Kind,
            op.OldName is null ? null : MetadataDispatch.GetFieldName(typeof(T), op.OldName, schema),
            op.TargetField is null ? null : MetadataDispatch.GetFieldName(typeof(T), op.TargetField, schema),
            op.InsertIndex);

}
