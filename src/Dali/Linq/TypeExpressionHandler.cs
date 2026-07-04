namespace Dali;

/// <summary>
/// Transforms <see cref="SurrealTypeFunctions"/> marker class method calls into SurrealQL type:: function expressions.
/// Invoked from <see cref="SurrealExpressionVisitor.TranslateCondition(System.Linq.Expressions.Expression, SurrealCommandBuilder)"/>
/// via <c>m.Method.DeclaringType == typeof(SurrealTypeFunctions)</c> dispatch.
/// </summary>
/// <remarks>
/// <b>Canonical handler class pattern for SurrealDB built-in function translation.</b>
///
/// Every function category handler follows this contract:
/// <list type="number">
/// <item><c>internal static class XxxExpressionHandler</c></item>
/// <item><c>public static string? TranslateXxxFunc(string methodName, string[] args)</c></item>
/// <item>Returns the SurrealQL expression string for known methods, <c>null</c> for unrecognized</item>
/// </list>
///
/// The visitor chains handlers: if one returns <c>null</c>, the next handler is tried.
/// This allows adding new categories without modifying existing handlers.
///
/// <b>Registration:</b> Add a <c>if (m.Method.DeclaringType == typeof(SurrealTypeFunctions))</c> branch
/// in <c>TranslateMethod</c>.
///
/// <b>Design rationale:</b> Marten uses <c>IMethodCallParser</c> (interface + registration list + caching)
/// for 30+ parser types with composite <c>ISqlFragment</c> output. Dali's functions are simpler:
/// <c>MethodCallExpression → string</c>. Handler classes scale well to ~300 functions.
/// Convert to a <c>List{ISurrealExpressionHandler}</c> registry only when handler count exceeds ~20.
/// </remarks>
internal static class TypeExpressionHandler
{
    /// <summary>
    /// Translates a <see cref="SurrealTypeFunctions"/> marker method call. Returns the SurrealQL expression, or null if unrecognized.
    /// </summary>
    /// <param name="methodName">The method name (e.g., "TypeBool", "IsString", "IsArray").</param>
    /// <param name="args">Pre-translated argument strings.</param>
    /// <returns>SurrealQL expression string, or null.</returns>
    public static string? TranslateTypeFunc(string methodName, string[] args)
    {
        return methodName switch
        {
            // ── Conversion functions (type::*) ──
            "TypeBool" when args.Length == 1 => $"type::bool({args[0]})",
            "TypeBytes" when args.Length == 1 => $"type::bytes({args[0]})",
            "TypeDatetime" when args.Length == 1 => $"type::datetime({args[0]})",
            "TypeDecimal" when args.Length == 1 => $"type::decimal({args[0]})",
            "TypeDuration" when args.Length == 1 => $"type::duration({args[0]})",
            "TypeFloat" when args.Length == 1 => $"type::float({args[0]})",
            "TypeInt" when args.Length == 1 => $"type::int({args[0]})",
            "TypeNumber" when args.Length == 1 => $"type::number({args[0]})",
            "TypePoint" when args.Length == 1 => $"type::point({args[0]})",
            "TypeString" when args.Length == 1 => $"type::string({args[0]})",
            "TypeTable" when args.Length == 1 => $"type::table({args[0]})",
            "TypeThing" when args.Length == 1 => $"type::thing({args[0]})",
            "TypeRecord" when args.Length == 1 => $"type::record({args[0]})",
            "TypeOf" when args.Length == 1 => $"type::of({args[0]})",

            // ── Checker functions (type::is_*) ──
            "IsArray" when args.Length == 1 => $"type::is_array({args[0]})",
            "IsBool" when args.Length == 1 => $"type::is_bool({args[0]})",
            "IsBytes" when args.Length == 1 => $"type::is_bytes({args[0]})",
            "IsCollection" when args.Length == 1 => $"type::is_collection({args[0]})",
            "IsDatetime" when args.Length == 1 => $"type::is_datetime({args[0]})",
            "IsDecimal" when args.Length == 1 => $"type::is_decimal({args[0]})",
            "IsDuration" when args.Length == 1 => $"type::is_duration({args[0]})",
            "IsFloat" when args.Length == 1 => $"type::is_float({args[0]})",
            "IsGeometry" when args.Length == 1 => $"type::is_geometry({args[0]})",
            "IsInt" when args.Length == 1 => $"type::is_int({args[0]})",
            "IsLine" when args.Length == 1 => $"type::is_line({args[0]})",
            "IsNone" when args.Length == 1 => $"type::is_none({args[0]})",
            "IsNull" when args.Length == 1 => $"type::is_null({args[0]})",
            "IsMultiline" when args.Length == 1 => $"type::is_multiline({args[0]})",
            "IsMultipoint" when args.Length == 1 => $"type::is_multipoint({args[0]})",
            "IsMultipolygon" when args.Length == 1 => $"type::is_multipolygon({args[0]})",
            "IsNumber" when args.Length == 1 => $"type::is_number({args[0]})",
            "IsObject" when args.Length == 1 => $"type::is_object({args[0]})",
            "IsPoint" when args.Length == 1 => $"type::is_point({args[0]})",
            "IsPolygon" when args.Length == 1 => $"type::is_polygon({args[0]})",
            "IsRange" when args.Length == 1 => $"type::is_range({args[0]})",
            "IsRecord" when args.Length == 1 => $"type::is_record({args[0]})",
            "IsString" when args.Length == 1 => $"type::is_string({args[0]})",
            "IsUuid" when args.Length == 1 => $"type::is_uuid({args[0]})",

            _ => null
        };
    }
}
