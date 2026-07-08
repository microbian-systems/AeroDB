namespace AeroDB;

public enum OperationKind
{
    Set,
    Increment,
    Append,
    AppendIfNotExists,
    Insert,
    InsertIfNotExists,
    Remove,
    Duplicate,
    Rename,
    Delete
}

internal class SetOperation
{
    public string FieldName { get; }
    public object? Value { get; }
    public OperationKind Kind { get; }
    public string? OldName { get; }      // for Rename
    public string? TargetField { get; }  // for Duplicate
    public int? InsertIndex { get; }     // for Insert / InsertIfNotExists

    public SetOperation(string fieldName, object? value, OperationKind kind,
        string? oldName = null, string? targetField = null, int? insertIndex = null)
    {
        FieldName = fieldName;
        Value = value;
        Kind = kind;
        OldName = oldName;
        TargetField = targetField;
        InsertIndex = insertIndex;
    }

    public string ToSurrealQL()
    {
        var fieldName = Escape(FieldName);

        return Kind switch
        {
            OperationKind.Set => $"{fieldName} = {FormatValue(Value)}",
            OperationKind.Increment => $"{fieldName} += {FormatValue(Value)}",
            OperationKind.Append => $"{fieldName} = array::insert({fieldName}, {FormatValue(Value)})",
            OperationKind.AppendIfNotExists => $"{fieldName} = {FormatAppendIfNotExists()}",
            OperationKind.Insert => InsertIndex is null
                ? $"{fieldName} += [{FormatValue(Value)}]"
                : $"{fieldName} = array::insert({fieldName}, {FormatValue(Value)}, {InsertIndex})",
            OperationKind.InsertIfNotExists => $"{fieldName} = {FormatInsertIfNotExists()}",
            OperationKind.Remove => $"{fieldName} = array::filter({fieldName}, |$value| $value != {FormatValue(Value)})",
            OperationKind.Duplicate => $"{fieldName} = {Escape(TargetField!)}",
            OperationKind.Rename => $"DROP $_; ALTER TABLE $_ RENAME COLUMN {Escape(OldName!)} TO {fieldName}",
            OperationKind.Delete => $"{fieldName} = NONE",
            _ => throw new InvalidOperationException($"Unknown operation kind: {Kind}")
        };
    }

    private string FormatAppendIfNotExists()
    {
        // array::add appends only when the value is not already present.
        var fieldName = Escape(FieldName);
        return $"array::add({fieldName}, {FormatValue(Value)})";
    }

    private string FormatInsertIfNotExists()
    {
        // IF array::find_index(field, value) IS NONE THEN array::insert(field, value, [index]) ELSE field END
        var fieldName = Escape(FieldName);
        var insertExpr = InsertIndex is null
            ? $"array::insert({fieldName}, {FormatValue(Value)})"
            : $"array::insert({fieldName}, {FormatValue(Value)}, {InsertIndex})";
        return $"IF {FuncFindIndex()} IS NONE THEN {insertExpr} ELSE {fieldName} END";
    }

    private string FuncFindIndex()
    {
        return $"array::find_index({Escape(FieldName)}, {FormatValue(Value)})";
    }

    private static string Escape(string name)
    {
        if (name.StartsWith("`")) return name;
        return $"`{name}`";
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
