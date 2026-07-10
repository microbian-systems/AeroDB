using System.Reflection;

namespace AeroDB.Sable;

/// <summary>
/// A cached plan for a compiled query type. Stores the SurrealQL skeleton
/// (with sentinel parameter values) and the mappings needed to substitute
/// live property values at execution time.
/// </summary>
public class CompiledPlan
{
    /// <summary>
    /// The SurrealQL skeleton produced by the <see cref="SurrealExpressionVisitor"/>.
    /// Contains sentinel values in <see cref="SurrealQueryResult.Limit"/> and
    /// <see cref="SurrealQueryResult.Skip"/> slots and sentinel parameter values
    /// in <see cref="SurrealQueryResult.Parameters"/>.
    /// </summary>
    public SurrealQueryResult SkeletonResult { get; internal set; } = null!;

    /// <summary>
    /// Maps SurrealQL parameter keys (e.g. <c>"p0"</c>) to the compiled query
    /// property name (e.g. <c>"FirstName"</c>).
    /// </summary>
    public Dictionary<string, string> ParameterMapping { get; internal set; } = new();

    /// <summary>
    /// If the compiled query uses a property for LIMIT (via <c>Take</c>),
    /// the name of that property on the query type. <c>null</c> if LIMIT is
    /// a literal or not present.
    /// </summary>
    public string? LimitProperty { get; internal set; }

    /// <summary>
    /// If the compiled query uses a property for START (via <c>Skip</c>),
    /// the name of that property on the query type. <c>null</c> if SKIP is
    /// a literal or not present.
    /// </summary>
    public string? SkipProperty { get; internal set; }

    /// <summary>
    /// All readable public properties on the compiled query type (excluding
    /// those decorated with <see cref="AeroDBIgnoreAttribute"/>).
    /// </summary>
    public PropertyInfo[] Properties { get; internal set; } = [];

    /// <summary>
    /// When <c>true</c>, the query returns a single result (not a list).
    /// A <c>LIMIT 1</c> is applied at execution time.
    /// </summary>
    public bool IsSingleResult { get; internal set; }
}
