namespace AeroDB.Sable;

/// <summary>
/// Tracks parameterized query values. Each call to <see cref="Parameter"/>
/// returns a unique $pN placeholder string and records the value for later
/// transmission alongside the SurrealQL string via <see cref="Parameters"/>.
/// </summary>
public class SurrealCommandBuilder
{
    private readonly Dictionary<string, object?> _parameters = new();
    private int _index;

    /// <summary>Parameter dictionary ready for RawQuery(sql, parameters).</summary>
    public IReadOnlyDictionary<string, object?> Parameters => _parameters;

    /// <summary>Whether any parameters have been recorded.</summary>
    public bool HasParameters => _parameters.Count > 0;

    /// <summary>
    /// Returns a parameter placeholder string (<c>$p0</c>, <c>$p1</c>, ...)
    /// and records the value in the parameter dictionary.
    /// Note: dictionary keys omit the <c>$</c> prefix (e.g., <c>"p0"</c>)
    /// to match SurrealDbClient expectations.
    /// </summary>
    public string Parameter(object? value)
    {
        var name = $"p{_index++}";
        _parameters[name] = value;
        return $"${name}";
    }
}
