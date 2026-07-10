namespace AeroDB.Sable;

/// <summary>
/// SurrealDB rand functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealRandFunctions
{
    /// <summary>Generates a random UUID v4. Maps to <c>rand::uuid::v4()</c>.</summary>
    public static string UuidV4() => throw new NotSupportedException("SurrealRandFunctions.UuidV4 can only be used inside a LINQ expression.");

    /// <summary>Generates a random UUID v7. Maps to <c>rand::uuid::v7()</c>.</summary>
    public static string UuidV7() => throw new NotSupportedException("SurrealRandFunctions.UuidV7 can only be used inside a LINQ expression.");

    /// <summary>Generates a random ULID. Maps to <c>rand::ulid()</c>.</summary>
    public static string Ulid() => throw new NotSupportedException("SurrealRandFunctions.Ulid can only be used inside a LINQ expression.");
    /// <summary>Generates a random integer in [min, max]. Maps to <c>rand::int(min, max)</c>.</summary>
    public static int Int(int min, int max) => throw new NotSupportedException("SurrealRandFunctions.Int can only be used inside a LINQ expression.");
    /// <summary>Generates a random float in [min, max]. Maps to <c>rand::float(min, max)</c>.</summary>
    public static double Float(double min, double max) => throw new NotSupportedException("SurrealRandFunctions.Float can only be used inside a LINQ expression.");
    /// <summary>Generates a random string of given length. Maps to <c>rand::string(len)</c>.</summary>
    public static string String(int len) => throw new NotSupportedException("SurrealRandFunctions.String can only be used inside a LINQ expression.");
    /// <summary>Generates a random boolean. Maps to <c>rand::bool()</c>.</summary>
    public static bool Bool() => throw new NotSupportedException("SurrealRandFunctions.Bool can only be used inside a LINQ expression.");
    /// <summary>Generates a random GUID. Maps to <c>rand::guid()</c>.</summary>
    public static string Guid() => throw new NotSupportedException("SurrealRandFunctions.Guid can only be used inside a LINQ expression.");
    /// <summary>Picks a random value from a list. Maps to <c>rand::enum(v1, v2, ...)</c>.</summary>
    public static string Enum(params string[] values) => throw new NotSupportedException("SurrealRandFunctions.Enum can only be used inside a LINQ expression.");
}
