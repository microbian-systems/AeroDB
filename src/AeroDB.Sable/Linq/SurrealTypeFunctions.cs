namespace AeroDB.Sable;

/// <summary>
/// SurrealDB type conversion and type-check functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealTypeFunctions
{
    // ── Conversion functions (type::* — return the value cast to the named type) ──

    /// <summary>Converts a value to bool. Maps to <c>type::bool(value)</c>.</summary>
    public static object TypeBool(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeBool can only be used inside a LINQ expression.");
    /// <summary>Converts a value to bytes. Maps to <c>type::bytes(value)</c>.</summary>
    public static object TypeBytes(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeBytes can only be used inside a LINQ expression.");
    /// <summary>Converts a value to datetime. Maps to <c>type::datetime(value)</c>.</summary>
    public static object TypeDatetime(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeDatetime can only be used inside a LINQ expression.");
    /// <summary>Converts a value to decimal. Maps to <c>type::decimal(value)</c>.</summary>
    public static object TypeDecimal(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeDecimal can only be used inside a LINQ expression.");
    /// <summary>Converts a value to duration. Maps to <c>type::duration(value)</c>.</summary>
    public static object TypeDuration(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeDuration can only be used inside a LINQ expression.");
    /// <summary>Converts a value to float. Maps to <c>type::float(value)</c>.</summary>
    public static object TypeFloat(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeFloat can only be used inside a LINQ expression.");
    /// <summary>Converts a value to int. Maps to <c>type::int(value)</c>.</summary>
    public static object TypeInt(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeInt can only be used inside a LINQ expression.");
    /// <summary>Converts a value to number. Maps to <c>type::number(value)</c>.</summary>
    public static object TypeNumber(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeNumber can only be used inside a LINQ expression.");
    /// <summary>Converts a value to point. Maps to <c>type::point(value)</c>.</summary>
    public static object TypePoint(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypePoint can only be used inside a LINQ expression.");
    /// <summary>Converts a value to string. Maps to <c>type::string(value)</c>.</summary>
    public static object TypeString(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeString can only be used inside a LINQ expression.");
    /// <summary>Converts a value to table. Maps to <c>type::table(value)</c>.</summary>
    public static object TypeTable(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeTable can only be used inside a LINQ expression.");
    /// <summary>Converts a value to thing. Maps to <c>type::thing(value)</c>.</summary>
    public static object TypeThing(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeThing can only be used inside a LINQ expression.");
    /// <summary>Converts a value to record. Maps to <c>type::record(value)</c>.</summary>
    public static object TypeRecord(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeRecord can only be used inside a LINQ expression.");
    /// <summary>Returns the type name of a value as a string. Maps to <c>type::of(value)</c>.</summary>
    public static string TypeOf(object value) => throw new NotSupportedException("SurrealTypeFunctions.TypeOf can only be used inside a LINQ expression.");

    // ── Checker functions (type::is_* — return bool) ──

    /// <summary>Checks if a value is an array. Maps to <c>type::is_array(value)</c>.</summary>
    public static bool IsArray(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsArray can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a bool. Maps to <c>type::is_bool(value)</c>.</summary>
    public static bool IsBool(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsBool can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is bytes. Maps to <c>type::is_bytes(value)</c>.</summary>
    public static bool IsBytes(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsBytes can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a collection. Maps to <c>type::is_collection(value)</c>.</summary>
    public static bool IsCollection(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsCollection can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a datetime. Maps to <c>type::is_datetime(value)</c>.</summary>
    public static bool IsDatetime(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsDatetime can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a decimal. Maps to <c>type::is_decimal(value)</c>.</summary>
    public static bool IsDecimal(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsDecimal can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a duration. Maps to <c>type::is_duration(value)</c>.</summary>
    public static bool IsDuration(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsDuration can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a float. Maps to <c>type::is_float(value)</c>.</summary>
    public static bool IsFloat(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsFloat can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a geometry. Maps to <c>type::is_geometry(value)</c>.</summary>
    public static bool IsGeometry(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsGeometry can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is an int. Maps to <c>type::is_int(value)</c>.</summary>
    public static bool IsInt(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsInt can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a line. Maps to <c>type::is_line(value)</c>.</summary>
    public static bool IsLine(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsLine can only be used inside a LINQ expression.");
    /// <summary>
    /// Checks if a value is NONE (missing/absent). Equivalent to <c>x.Field == null</c> in LINQ expressions.
    /// SurrealDB distinguishes NONE (absent) from NULL (JSON null). See <see cref="IsNull"/> for NULL checking.
    /// Maps to <c>type::is_none(value)</c>.
    /// </summary>
    public static bool IsNone(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsNone can only be used inside a LINQ expression.");
    /// <summary>
    /// Checks if a value is NULL (JSON null). This is NOT equivalent to <c>x.Field == null</c> in LINQ,
    /// which checks for NONE (absent). Use this for explicit SurrealDB NULL checking.
    /// Maps to <c>type::is_null(value)</c>.
    /// </summary>
    public static bool IsNull(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsNull can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a multiline. Maps to <c>type::is_multiline(value)</c>.</summary>
    public static bool IsMultiline(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsMultiline can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a multipoint. Maps to <c>type::is_multipoint(value)</c>.</summary>
    public static bool IsMultipoint(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsMultipoint can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a multipolygon. Maps to <c>type::is_multipolygon(value)</c>.</summary>
    public static bool IsMultipolygon(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsMultipolygon can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a number. Maps to <c>type::is_number(value)</c>.</summary>
    public static bool IsNumber(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsNumber can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is an object. Maps to <c>type::is_object(value)</c>.</summary>
    public static bool IsObject(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsObject can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a point. Maps to <c>type::is_point(value)</c>.</summary>
    public static bool IsPoint(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsPoint can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a polygon. Maps to <c>type::is_polygon(value)</c>.</summary>
    public static bool IsPolygon(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsPolygon can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a range. Maps to <c>type::is_range(value)</c>.</summary>
    public static bool IsRange(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsRange can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a record. Maps to <c>type::is_record(value)</c>.</summary>
    public static bool IsRecord(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsRecord can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a string. Maps to <c>type::is_string(value)</c>.</summary>
    public static bool IsString(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsString can only be used inside a LINQ expression.");
    /// <summary>Checks if a value is a uuid. Maps to <c>type::is_uuid(value)</c>.</summary>
    public static bool IsUuid(object value) => throw new NotSupportedException("SurrealTypeFunctions.IsUuid can only be used inside a LINQ expression.");
}
