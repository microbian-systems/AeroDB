namespace AeroDB.Sable;

/// <summary>
/// SurrealDB string functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealStringFunctions
{
    /// <summary>Repeats a string n times. Maps to <c>string::repeat(s, n)</c>.</summary>
    public static string Repeat(string s, int n) => throw new NotSupportedException("SurrealStringFunctions.Repeat can only be used inside a LINQ expression.");

    /// <summary>Reverses a string. Maps to <c>string::reverse(s)</c>.</summary>
    public static string Reverse(string s) => throw new NotSupportedException("SurrealStringFunctions.Reverse can only be used inside a LINQ expression.");

    /// <summary>Computes fuzzy similarity score. Maps to <c>string::similarity::fuzzy(a, b)</c>.</summary>
    public static int SimilarityFuzzy(string a, string b) => throw new NotSupportedException("SurrealStringFunctions.SimilarityFuzzy can only be used inside a LINQ expression.");

    /// <summary>Computes Jaro similarity. Maps to <c>string::similarity::jaro(a, b)</c>.</summary>
    public static double SimilarityJaro(string a, string b) => throw new NotSupportedException("SurrealStringFunctions.SimilarityJaro can only be used inside a LINQ expression.");

    /// <summary>Computes Jaro-Winkler similarity. Maps to <c>string::similarity::jaro_winkler(a, b)</c>.</summary>
    public static double SimilarityJaroWinkler(string a, string b) => throw new NotSupportedException("SurrealStringFunctions.SimilarityJaroWinkler can only be used inside a LINQ expression.");

    /// <summary>Computes Levenshtein distance. Maps to <c>string::distance::levenshtein(a, b)</c>.</summary>
    public static int DistanceLevenshtein(string a, string b) => throw new NotSupportedException("SurrealStringFunctions.DistanceLevenshtein can only be used inside a LINQ expression.");

    /// <summary>Computes Hamming distance. Maps to <c>string::distance::hamming(a, b)</c>.</summary>
    public static int DistanceHamming(string a, string b) => throw new NotSupportedException("SurrealStringFunctions.DistanceHamming can only be used inside a LINQ expression.");

    /// <summary>Computes Damerau-Levenshtein distance. Maps to <c>string::distance::damerau_levenshtein(a, b)</c>.</summary>
    public static int DistanceDamerauLevenshtein(string a, string b) => throw new NotSupportedException("SurrealStringFunctions.DistanceDamerauLevenshtein can only be used inside a LINQ expression.");

    /// <summary>Computes normalized Levenshtein distance. Maps to <c>string::distance::normalized_levenshtein(a, b)</c>.</summary>
    public static double DistanceNormalizedLevenshtein(string a, string b) => throw new NotSupportedException("SurrealStringFunctions.DistanceNormalizedLevenshtein can only be used inside a LINQ expression.");

    /// <summary>Computes normalized Damerau-Levenshtein distance. Maps to <c>string::distance::normalized_damerau_levenshtein(a, b)</c>.</summary>
    public static double DistanceNormalizedDamerauLevenshtein(string a, string b) => throw new NotSupportedException("SurrealStringFunctions.DistanceNormalizedDamerauLevenshtein can only be used inside a LINQ expression.");

    /// <summary>Computes OSA (Optimal String Alignment) distance. Maps to <c>string::distance::osa(a, b)</c>.</summary>
    public static int DistanceOsa(string a, string b) => throw new NotSupportedException("SurrealStringFunctions.DistanceOsa can only be used inside a LINQ expression.");

    /// <summary>Joins string items with a separator. Maps to <c>string::join(sep, items...)</c>.</summary>
    public static string Join(string separator, params string[] items) => throw new NotSupportedException("SurrealStringFunctions.Join can only be used inside a LINQ expression.");

    /// <summary>Checks if a string contains only alphanumeric characters. Maps to <c>string::is::alphanum(s)</c>.</summary>
    public static bool IsAlphanum(string s) => throw new NotSupportedException("SurrealStringFunctions.IsAlphanum can only be used inside a LINQ expression.");

    /// <summary>Checks if a string contains only alphabetic characters. Maps to <c>string::is::alpha(s)</c>.</summary>
    public static bool IsAlpha(string s) => throw new NotSupportedException("SurrealStringFunctions.IsAlpha can only be used inside a LINQ expression.");

    /// <summary>Checks if a string contains only ASCII characters. Maps to <c>string::is::ascii(s)</c>.</summary>
    public static bool IsAscii(string s) => throw new NotSupportedException("SurrealStringFunctions.IsAscii can only be used inside a LINQ expression.");

    /// <summary>Checks if a string is a valid email. Maps to <c>string::is::email(s)</c>.</summary>
    public static bool IsEmail(string s) => throw new NotSupportedException("SurrealStringFunctions.IsEmail can only be used inside a LINQ expression.");

    /// <summary>Checks if a string is a valid URL. Maps to <c>string::is::url(s)</c>.</summary>
    public static bool IsUrl(string s) => throw new NotSupportedException("SurrealStringFunctions.IsUrl can only be used inside a LINQ expression.");

    /// <summary>Checks if a string is a valid UUID. Maps to <c>string::is::uuid(s)</c>.</summary>
    public static bool IsUuid(string s) => throw new NotSupportedException("SurrealStringFunctions.IsUuid can only be used inside a LINQ expression.");

    /// <summary>Checks if a string is numeric. Maps to <c>string::is::numeric(s)</c>.</summary>
    public static bool IsNumeric(string s) => throw new NotSupportedException("SurrealStringFunctions.IsNumeric can only be used inside a LINQ expression.");

    /// <summary>Checks if a string is a valid datetime. Maps to <c>string::is::datetime(s)</c>.</summary>
    public static bool IsDatetime(string s) => throw new NotSupportedException("SurrealStringFunctions.IsDatetime can only be used inside a LINQ expression.");
}
