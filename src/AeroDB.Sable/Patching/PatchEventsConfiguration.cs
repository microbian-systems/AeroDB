using System.Linq.Expressions;

namespace AeroDB.Sable;

/// <summary>
/// Fluent configuration for mapping patch operations on a specific document type to domain events.
/// Accessed via <see cref="DocumentMapping{T}.PatchEvents"/>.
/// </summary>
public sealed class PatchEventsConfiguration<T>
{
    private readonly List<PatchEventRule> _rules = new();
    internal IReadOnlyList<PatchEventRule> Rules => _rules;

    /// <summary>
    /// Configures a rule that fires when a field is incremented.
    /// </summary>
    public IncrementRuleBuilder<T, TValue> OnIncrement<TValue>(Expression<Func<T, TValue>> property)
    {
        var memberName = GetMemberName(property);
        return new IncrementRuleBuilder<T, TValue>(this, memberName, OperationKind.Increment);
    }

    /// <summary>
    /// Configures a rule that fires when a field is decremented.
    /// (Internally mapped to Increment with negative amount.)
    /// </summary>
    public IncrementRuleBuilder<T, TValue> OnDecrement<TValue>(Expression<Func<T, TValue>> property)
    {
        var memberName = GetMemberName(property);
        return new IncrementRuleBuilder<T, TValue>(this, memberName, OperationKind.Increment);
    }

    /// <summary>
    /// Configures a rule that fires when a field is replaced (set to a new value).
    /// </summary>
    public ReplaceRuleBuilder<T, TValue> OnReplace<TValue>(Expression<Func<T, TValue>> property)
    {
        var memberName = GetMemberName(property);
        return new ReplaceRuleBuilder<T, TValue>(this, memberName, OperationKind.Set);
    }

    internal void AddRule(PatchEventRule rule)
    {
        _rules.Add(rule);
    }

    private static string GetMemberName<TValue>(Expression<Func<T, TValue>> expression)
    {
        return expression.Body switch
        {
            MemberExpression me => me.Member.Name,
            UnaryExpression { Operand: MemberExpression me } => me.Member.Name,
            _ => throw new ArgumentException("Expression must be a property access.", nameof(expression))
        };
    }
}

/// <summary>
/// Builder for increment/decrement patch → event rules.
/// </summary>
public sealed class IncrementRuleBuilder<TEntity, TValue>
{
    private readonly PatchEventsConfiguration<TEntity> _parent;
    private readonly string _fieldName;
    private readonly OperationKind _operationKind;
    private string? _reason;

    internal IncrementRuleBuilder(PatchEventsConfiguration<TEntity> parent, string fieldName, OperationKind operationKind)
    {
        _parent = parent;
        _fieldName = fieldName;
        _operationKind = operationKind;
    }

    /// <summary>
    /// Sets the business reason that triggers this rule (matched case-insensitively).
    /// </summary>
    public IncrementRuleBuilder<TEntity, TValue> WithReason(string reason)
    {
        _reason = reason;
        return this;
    }

    /// <summary>
    /// Defines the domain event emitted when this rule matches.
    /// The factory receives: (entity, amount, patchContext) → domainEvent
    /// </summary>
    public PatchEventsConfiguration<TEntity> Emit(Func<TEntity?, TValue?, PatchContext, object> eventFactory)
    {
        var rule = new PatchEventRule
        {
            EntityType = typeof(TEntity),
            FieldName = _fieldName,
            OperationKind = _operationKind,
            Reason = _reason!,
            EventType = eventFactory.Method.ReturnType,
            EventFactory = (doc, val, ctx) => eventFactory((TEntity?)doc, (TValue?)val, ctx)
        };
        _parent.AddRule(rule);
        return _parent;
    }
}

/// <summary>
/// Builder for replace/set patch → event rules.
/// </summary>
public sealed class ReplaceRuleBuilder<TEntity, TValue>
{
    private readonly PatchEventsConfiguration<TEntity> _parent;
    private readonly string _fieldName;
    private readonly OperationKind _operationKind;
    private string? _reason;
    private Func<TEntity?, TEntity?, PatchContext, bool>? _when;

    internal ReplaceRuleBuilder(PatchEventsConfiguration<TEntity> parent, string fieldName, OperationKind operationKind)
    {
        _parent = parent;
        _fieldName = fieldName;
        _operationKind = operationKind;
    }

    /// <summary>
    /// Sets the business reason that triggers this rule (matched case-insensitively).
    /// </summary>
    public ReplaceRuleBuilder<TEntity, TValue> WithReason(string reason)
    {
        _reason = reason;
        return this;
    }

    /// <summary>
    /// Adds an optional predicate filter. The rule only matches when this returns true.
    /// Warning: evaluates against snapshot isolation — requires UseOptimisticConcurrency = true for safety.
    /// </summary>
    public ReplaceRuleBuilder<TEntity, TValue> When(Func<TEntity?, TEntity?, PatchContext, bool> predicate)
    {
        _when = predicate;
        return this;
    }

    /// <summary>
    /// Defines the domain event emitted when this rule matches.
    /// The factory receives: (beforeEntity, afterEntity, patchContext) → domainEvent
    /// </summary>
    public PatchEventsConfiguration<TEntity> Emit(Func<TEntity?, TEntity?, PatchContext, object> eventFactory)
    {
        var rule = new PatchEventRule
        {
            EntityType = typeof(TEntity),
            FieldName = _fieldName,
            OperationKind = _operationKind,
            Reason = _reason!,
            EventType = eventFactory.Method.ReturnType,
            EventFactory = (before, after, ctx) =>
            {
                // Check When predicate if configured
                if (_when is not null && !_when((TEntity?)before, (TEntity?)after, ctx))
                    return null!;
                return eventFactory((TEntity?)before, (TEntity?)after, ctx);
            }
        };
        _parent.AddRule(rule);
        return _parent;
    }
}
