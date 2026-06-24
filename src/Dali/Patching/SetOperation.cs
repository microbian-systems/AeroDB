namespace Dali;

internal enum OperationKind
{
    Set,
    Increment,
    Append,
    Delete
}

internal class SetOperation
{
    public string FieldName { get; }
    public object? Value { get; }
    public OperationKind Kind { get; }

    public SetOperation(string fieldName, object? value, OperationKind kind)
    {
        FieldName = fieldName;
        Value = value;
        Kind = kind;
    }

    public string ToSurrealQL()
    {
        return Kind switch
        {
            OperationKind.Set => $"{FieldName} = {FormatValue(Value)}",
            OperationKind.Increment => $"{FieldName} += {FormatValue(Value)}",
            OperationKind.Append => $"{FieldName} += [{FormatValue(Value)}]",
            OperationKind.Delete => $"{FieldName} = NONE",
            _ => throw new InvalidOperationException($"Unknown operation kind: {Kind}")
        };
    }

    private static string FormatValue(object? val) => val switch
    {
        null => "NONE",
        string s => $"'{s.Replace("'", "\\'")}'",
        bool b => b ? "true" : "false",
        int or long or short or byte or float or double or decimal =>
            ((IFormattable)val).ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        DateTime dt => $"d'{dt:yyyy-MM-ddTHH:mm:ss}'",
        DateTimeOffset dto => $"d'{dto:yyyy-MM-ddTHH:mm:ss}'",
        _ => $"'{val}'"
    };
}
