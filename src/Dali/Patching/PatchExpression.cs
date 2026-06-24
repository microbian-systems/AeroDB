using System.Linq.Expressions;
using System.Reflection;
using Dali.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dali;

/// <summary>
/// Fluent builder for partial document updates (patch operations).
/// Collects SET/INCREMENT/APPEND/DELETE operations and generates a SurrealQL
/// UPDATE statement when <see cref="ApplyAsync"/> is called.
/// </summary>
/// <typeparam name="T">The document type (must extend <c>Record</c> from SurrealDb.Net.Models).</typeparam>
public class PatchExpression<T> where T : class
{
    private readonly IDocumentSession _session;
    private readonly string _recordId;
    private readonly List<SetOperation> _operations = new();
    private readonly ILogger<PatchExpression<T>> _logger;

    internal PatchExpression(IDocumentSession session, string recordId)
    {
        _session = session;
        _recordId = recordId;
        _logger = ((InternalSessionBase)session).StoreOptions.LoggerFactory
            ?.CreateLogger<PatchExpression<T>>()
            ?? NullLogger<PatchExpression<T>>.Instance;
    }

    /// <summary>
    /// Sets a field to the specified value.
    /// </summary>
    /// <param name="property">Expression selecting the property to set.</param>
    /// <param name="value">The new value.</param>
    public PatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> property, TValue value)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, value, OperationKind.Set));
        return this;
    }

    /// <summary>
    /// Increments a numeric field by the given amount.
    /// </summary>
    /// <param name="property">Expression selecting the numeric property.</param>
    /// <param name="amount">The amount to add.</param>
    public PatchExpression<T> Increment(Expression<Func<T, int>> property, int amount)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    /// <summary>
    /// Increments a numeric field by the given amount.
    /// </summary>
    /// <param name="property">Expression selecting the numeric property.</param>
    /// <param name="amount">The amount to add.</param>
    public PatchExpression<T> Increment(Expression<Func<T, long>> property, long amount)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    /// <summary>
    /// Increments a numeric field by the given amount.
    /// </summary>
    /// <param name="property">Expression selecting the numeric property.</param>
    /// <param name="amount">The amount to add.</param>
    public PatchExpression<T> Increment(Expression<Func<T, decimal>> property, decimal amount)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    /// <summary>
    /// Increments a numeric field by the given amount.
    /// </summary>
    /// <param name="property">Expression selecting the numeric property.</param>
    /// <param name="amount">The amount to add.</param>
    public PatchExpression<T> Increment(Expression<Func<T, float>> property, float amount)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    /// <summary>
    /// Increments a numeric field by the given amount.
    /// </summary>
    /// <param name="property">Expression selecting the numeric property.</param>
    /// <param name="amount">The amount to add.</param>
    public PatchExpression<T> Increment(Expression<Func<T, double>> property, double amount)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, amount, OperationKind.Increment));
        return this;
    }

    /// <summary>
    /// Appends a value to an array field.
    /// </summary>
    /// <param name="property">Expression selecting the array property.</param>
    /// <param name="item">The item to append.</param>
    public PatchExpression<T> Append<TValue>(Expression<Func<T, IEnumerable<TValue>>> property, TValue item)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, item, OperationKind.Append));
        return this;
    }

    /// <summary>
    /// Deletes a field (sets to NONE in SurrealDB).
    /// </summary>
    /// <param name="property">Expression selecting the property to delete.</param>
    public PatchExpression<T> Delete<TValue>(Expression<Func<T, TValue>> property)
    {
        var member = GetMember(property);
        _operations.Add(new SetOperation(member.Name, null, OperationKind.Delete));
        return this;
    }

    /// <summary>
    /// Applies all queued patch operations by executing a SurrealQL UPDATE statement.
    /// After successful execution, the operation queue is cleared.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        if (_operations.Count == 0) return;

        var table = MetadataDispatch.GetTableName(typeof(T));
        var sets = _operations.Select(o => o.ToSurrealQL()).ToList();
        var surql = $"UPDATE {table}:{_recordId} SET {string.Join(", ", sets)};";

        _logger.LogDebug("Applying patch: {SurrealQL}", surql);

        var surrealdbSession = ((InternalSessionBase)_session).Session;
        await surrealdbSession.RawQuery(surql, null, ct).ConfigureAwait(false);

        _operations.Clear();
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

    private static string Snake(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
