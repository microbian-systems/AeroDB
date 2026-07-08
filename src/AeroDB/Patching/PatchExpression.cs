using System.Linq.Expressions;
using System.Reflection;
using AeroDB.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroDB;

/// <summary>
/// Fluent builder for partial document updates (patch operations).
/// Collects SET/INCREMENT/APPEND/DELETE operations and generates a SurrealQL
/// UPDATE statement when <see cref="ApplyAsync"/> is called.
/// </summary>
/// <typeparam name="T">The document type (must extend <c>Record</c> from SurrealDb.Net.Models).</typeparam>
public class PatchExpression<T> : IPatchExpression<T>, IDeferredPatch where T : class
{
    private readonly IDocumentSession _session;
    private readonly string _recordId;
    private readonly List<SetOperation> _operations = new();
    private readonly ILogger<PatchExpression<T>> _logger;
    private PatchContext? _patchContext;

    internal PatchExpression(IDocumentSession session, string recordId)
    {
        _session = session;
        _recordId = recordId;
        _logger = ((InternalSessionBase)session).StoreOptions.LoggerFactory
            ?.CreateLogger<PatchExpression<T>>()
            ?? NullLogger<PatchExpression<T>>.Instance;

        // Auto-register on the session for execution during SaveChangesAsync
        if (session is DocumentSession ds)
            ds._queuedPatches.Add(this);
    }

    // ── Set ────────────────────────────────────────────────────────────

    public IPatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> property, TValue value)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, value, OperationKind.Set));
        return this;
    }

    // ── Increment ──────────────────────────────────────────────────────

    public IPatchExpression<T> Increment(Expression<Func<T, int>> property, int amount = 1)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    public IPatchExpression<T> Increment(Expression<Func<T, long>> property, long amount = 1)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    public IPatchExpression<T> Increment(Expression<Func<T, double>> property, double amount = 1)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    public IPatchExpression<T> Increment(Expression<Func<T, float>> property, float amount = 1)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    public IPatchExpression<T> Increment(Expression<Func<T, decimal>> property, decimal amount = 1)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    // ── Append ─────────────────────────────────────────────────────────

    public IPatchExpression<T> Append<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, element, OperationKind.Append));
        return this;
    }

    public IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, element, OperationKind.AppendIfNotExists));
        return this;
    }

    // ── Insert ─────────────────────────────────────────────────────────

    public IPatchExpression<T> Insert<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element, int? index = null)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, element, OperationKind.Insert, insertIndex: index));
        return this;
    }

    public IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element, int? index = null)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, element, OperationKind.InsertIfNotExists, insertIndex: index));
        return this;
    }

    // ── Remove ─────────────────────────────────────────────────────────

    public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, element, OperationKind.Remove));
        return this;
    }

    // ── Duplicate ──────────────────────────────────────────────────────

    public IPatchExpression<T> Duplicate<TElement>(Expression<Func<T, TElement>> source, params Expression<Func<T, TElement>>[] destinations)
    {
        var sourceMember = GetMember(source);
        foreach (var dest in destinations)
        {
            var destMember = GetMember(dest);
            _operations.Add(new SetOperation(destMember.Name, null, OperationKind.Duplicate, targetField: sourceMember.Name));
        }
        return this;
    }

    // ── Rename ─────────────────────────────────────────────────────────

    public IPatchExpression<T> Rename(string oldName, Expression<Func<T, object?>> target)
    {
        var member = GetMember(target);
        _operations.Add(new SetOperation(member.Name, null, OperationKind.Rename, oldName: oldName));
        return this;
    }

    // ── Delete ─────────────────────────────────────────────────────────

    public IPatchExpression<T> Delete<TValue>(Expression<Func<T, TValue>> property)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, null, OperationKind.Delete));
        return this;
    }

    // ── SetAll ──────────────────────────────────────────────────────────

    public IPatchExpression<T> SetAll<TValue>(TValue value)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.PropertyType == typeof(TValue));
        foreach (var prop in props)
            _operations.Add(new SetOperation(prop.Name, value, OperationKind.Set));
        return this;
    }

    // ── WithReason / WithUserId ──────────────────────────────────────

    public IPatchExpression<T> WithReason(string reason)
    {
        _patchContext ??= new PatchContext();
        _patchContext.Reason = reason;
        return this;
    }

    /// <summary>
    /// Attaches a user identity to this patch operation.
    /// The user ID is stored as metadata and available to <see cref="IDocumentSessionListener"/>
    /// implementations and future event pipelines.
    /// </summary>
    public IPatchExpression<T> WithUserId(string userId)
    {
        _patchContext ??= new PatchContext();
        _patchContext.UserId = userId;
        return this;
    }

    /// <summary>
    /// The patch context attached to this operation via <see cref="WithReason"/>.
    /// Null if no reason was specified.
    /// </summary>
    internal PatchContext? PatchContext => _patchContext;

    // ── Execute (IDeferredPatch) ──────────────────────────────────────

    /// <summary>
    /// Executes all queued patch operations by running a SurrealQL UPDATE statement.
    /// Called by the session during <see cref="IDocumentSession.SaveChangesAsync"/>.
    /// </summary>
    async Task IDeferredPatch.ExecuteAsync(IDocumentSession session, CancellationToken ct)
    {
        if (_operations.Count == 0) return;

        // Separate Rename operations (these need ALTER TABLE, not UPDATE SET)
        var renameOps = _operations.Where(o => o.Kind == OperationKind.Rename).ToList();
        var updateOps = _operations.Where(o => o.Kind != OperationKind.Rename).ToList();

        var internalSession = (InternalSessionBase)session;
        var schema = internalSession.StoreOptions.Schema;
        var table = MetadataDispatch.GetTableName(typeof(T), schema);
        var surrealdbSession = internalSession.Session;

        // Execute UPDATE SET for all non-rename operations
        if (updateOps.Count > 0)
        {
            var sets = updateOps.Select(o => MapOperation(o, schema).ToSurrealQL()).ToList();
            var surql = $"UPDATE {table}:{FormatRecordId(_recordId)} SET {string.Join(", ", sets)};";
            _logger.LogDebug("Applying patch: {SurrealQL}", surql);
            await surrealdbSession.RawQuery(surql, null, ct).ConfigureAwait(false);
        }

        // Execute ALTER TABLE RENAME COLUMN for rename operations
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
        await ((IDeferredPatch)this).ExecuteAsync(_session, ct).ConfigureAwait(false);
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

    private static string FormatRecordId(string id)
    {
        if (long.TryParse(id, out _) || ulong.TryParse(id, out _))
            return id;

        return $"`{id.Replace("`", "\\`")}`";
    }

}
