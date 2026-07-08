namespace AeroDB;

/// <summary>
/// Defines a rule that maps a patch operation on a specific entity field to a domain event.
/// Rules are registered via <see cref="PatchEventsConfiguration{T}"/> and resolved
/// by <see cref="PatchRuleResolver"/> at patch execution time.
/// </summary>
public sealed class PatchEventRule
{
    /// <summary>The CLR type of the entity being patched.</summary>
    public Type EntityType { get; init; } = default!;

    /// <summary>The CLR name of the field being patched.</summary>
    public string FieldName { get; init; } = default!;

    /// <summary>
    /// The operation kind that triggers this rule.
    /// Use values from <see cref="OperationKind"/>: Increment, Set, Append, Remove, etc.
    /// </summary>
    public OperationKind OperationKind { get; init; }

    /// <summary>The business reason string that triggers this rule. Matched case-insensitively.</summary>
    public string Reason { get; init; } = default!;

    /// <summary>The CLR type of the domain event to emit when this rule matches.</summary>
    public Type EventType { get; init; } = default!;

    /// <summary>
    /// Factory function that creates the domain event instance from patch data.
    /// Signature: (documentBefore, valueOrAmount, patchContext) → domainEvent
    /// </summary>
    public Func<object?, object?, PatchContext, object> EventFactory { get; init; } = default!;

    /// <summary>
    /// Composite key for rule matching: EntityType + FieldName + OperationKind + Reason.
    /// </summary>
    public string CompositeKey =>
        $"{EntityType.Name}|{FieldName}|{OperationKind}|{Reason.ToLowerInvariant()}";
}
