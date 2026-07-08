namespace AeroDB;

/// <summary>
/// Resolves <see cref="PatchEventRule"/> instances based on entity type, field name, operation kind, and reason.
/// Rules are registered during <see cref="DocumentStore.InitializeAsync"/> via <c>PatchEvents()</c> configuration.
/// </summary>
public sealed class PatchRuleResolver
{
    private readonly Dictionary<string, PatchEventRule> _rules = new();

    /// <summary>
    /// Registers a patch event rule. Rules are keyed by composite key: EntityType + FieldName + OperationKind + Reason.
    /// Duplicate keys (same entity, field, op, and reason) are silently replaced.
    /// </summary>
    public void Register(PatchEventRule rule)
    {
        var key = rule.CompositeKey;
        _rules[key] = rule;
    }

    /// <summary>
    /// Registers multiple rules at once.
    /// </summary>
    public void RegisterRange(IEnumerable<PatchEventRule> rules)
    {
        foreach (var rule in rules)
            Register(rule);
    }

    /// <summary>
    /// Attempts to resolve a matching rule for the given operation context.
    /// Returns null if no rule matches.
    /// </summary>
    public PatchEventRule? Resolve(Type entityType, string fieldName, OperationKind operationKind, string? reason)
    {
        if (reason is null)
            return null;

        var key = $"{entityType.Name}|{fieldName}|{operationKind}|{reason.ToLowerInvariant()}";
        return _rules.GetValueOrDefault(key);
    }

    /// <summary>
    /// Number of registered rules.
    /// </summary>
    public int Count => _rules.Count;
}
