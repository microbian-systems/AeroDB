namespace AeroDB.Sable;

/// <summary>
/// Transforms <see cref="SurrealArrayFunctions"/> marker class method calls into
/// SurrealQL expressions — either <c>array::*()</c> functions or SQL operators.
/// </summary>
internal static class ArrayExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealArrayFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    public static string? TranslateArrayFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            "Add" when args.Length == 2 => $"array::add({args[0]}, {args[1]})",
            "Append" when args.Length == 2 => $"array::append({args[0]}, {args[1]})",
            "Prepend" when args.Length == 2 => $"array::prepend({args[0]}, {args[1]})",
            "Remove" when args.Length == 2 => $"array::remove({args[0]}, {args[1]})",
            "Sort" when args.Length == 1 => $"array::sort({args[0]})",
            "SortAsc" when args.Length == 1 => $"array::sort({args[0]})",
            "SortDesc" when args.Length == 1 => $"array::sort::desc({args[0]})",
            "Reverse" when args.Length == 1 => $"array::reverse({args[0]})",
            "Distinct" when args.Length == 1 => $"array::distinct({args[0]})",
            "Union" when args.Length == 2 => $"array::union({args[0]}, {args[1]})",
            "Intersect" when args.Length == 2 => $"array::intersect({args[0]}, {args[1]})",
            "ArrayContains" when args.Length == 2 => $"array::contains({args[0]}, {args[1]})",
            "Flatten" when args.Length == 1 => $"array::flatten({args[0]})",
            "Len" when args.Length == 1 => $"array::len({args[0]})",

            // SurrealDB operators (not function calls)
            "ContainsAll"   when args.Length == 2 => $"{args[0]} CONTAINSALL {args[1]}",
            "ContainsAny"   when args.Length == 2 => $"{args[0]} CONTAINSANY {args[1]}",
            "ContainsNone"  when args.Length == 2 => $"{args[0]} CONTAINSNONE {args[1]}",
            "Intersects"    when args.Length == 2 => $"array::len(array::intersect({args[0]}, {args[1]})) > 0",
            _ => null
        };
    }
}
