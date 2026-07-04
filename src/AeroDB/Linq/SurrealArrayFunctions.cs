using System.Collections.Generic;

namespace AeroDB;

/// <summary>
/// SurrealDB array functions for use in LINQ expressions.
/// These methods are NOT callable at runtime — they exist for expression-tree translation only.
/// </summary>
public static class SurrealArrayFunctions
{
    /// <summary>Adds a value to an array. Maps to <c>array::add(arr, value)</c>.</summary>
    public static object[] Add<T>(T[] arr, T value) => throw new NotSupportedException("SurrealArrayFunctions.Add can only be used inside a LINQ expression.");
    /// <summary>Appends a value to an array. Maps to <c>array::append(arr, value)</c>.</summary>
    public static object[] Append<T>(T[] arr, T value) => throw new NotSupportedException("SurrealArrayFunctions.Append can only be used inside a LINQ expression.");
    /// <summary>Prepends a value to an array. Maps to <c>array::prepend(arr, value)</c>.</summary>
    public static object[] Prepend<T>(T[] arr, T value) => throw new NotSupportedException("SurrealArrayFunctions.Prepend can only be used inside a LINQ expression.");
    /// <summary>Removes an element at an index from an array. Maps to <c>array::remove(arr, index)</c>.</summary>
    public static object[] Remove<T>(T[] arr, int index) => throw new NotSupportedException("SurrealArrayFunctions.Remove can only be used inside a LINQ expression.");
    /// <summary>Sorts an array. Maps to <c>array::sort(arr)</c>.</summary>
    public static object[] Sort<T>(T[] arr) => throw new NotSupportedException("SurrealArrayFunctions.Sort can only be used inside a LINQ expression.");
    /// <summary>Sorts an array ascending. Maps to <c>array::sort(arr)</c>.</summary>
    public static object[] SortAsc<T>(T[] arr) => throw new NotSupportedException("SurrealArrayFunctions.SortAsc can only be used inside a LINQ expression.");
    /// <summary>Sorts an array descending. Maps to <c>array::sort::desc(arr)</c>.</summary>
    public static object[] SortDesc<T>(T[] arr) => throw new NotSupportedException("SurrealArrayFunctions.SortDesc can only be used inside a LINQ expression.");
    /// <summary>Reverses an array. Maps to <c>array::reverse(arr)</c>.</summary>
    public static object[] Reverse<T>(T[] arr) => throw new NotSupportedException("SurrealArrayFunctions.Reverse can only be used inside a LINQ expression.");
    /// <summary>Returns distinct elements from an array. Maps to <c>array::distinct(arr)</c>.</summary>
    public static object[] Distinct<T>(T[] arr) => throw new NotSupportedException("SurrealArrayFunctions.Distinct can only be used inside a LINQ expression.");
    /// <summary>Returns the union of two arrays. Maps to <c>array::union(arr1, arr2)</c>.</summary>
    public static object[] Union<T>(T[] arr1, T[] arr2) => throw new NotSupportedException("SurrealArrayFunctions.Union can only be used inside a LINQ expression.");
    /// <summary>Returns the intersection of two arrays. Maps to <c>array::intersect(arr1, arr2)</c>.</summary>
    public static object[] Intersect<T>(T[] arr1, T[] arr2) => throw new NotSupportedException("SurrealArrayFunctions.Intersect can only be used inside a LINQ expression.");
    /// <summary>Checks if an array contains a value. Maps to <c>array::contains(arr, value)</c>.</summary>
    public static bool ArrayContains<T>(T[] arr, T value) => throw new NotSupportedException("SurrealArrayFunctions.ArrayContains can only be used inside a LINQ expression.");
    /// <summary>Flattens a nested array. Maps to <c>array::flatten(arr)</c>.</summary>
    public static object[] Flatten<T>(T[][] arr) => throw new NotSupportedException("SurrealArrayFunctions.Flatten can only be used inside a LINQ expression.");
    /// <summary>Returns the length of an array. Maps to <c>array::len(arr)</c>.</summary>
    public static int Len<T>(T[] arr) => throw new NotSupportedException("SurrealArrayFunctions.Len can only be used inside a LINQ expression.");

    // ── Phase 16: CONTAINSALL / CONTAINSANY / CONTAINSNONE / INTERSECTS ──

    /// <summary>CONTAINSALL — true if the array contains all specified values. Maps to <c>array::contains_all(arr, values)</c>.</summary>
    public static bool ContainsAll<T>(IEnumerable<T>? array, IReadOnlyList<T> values) => throw new NotSupportedException("SurrealArrayFunctions.ContainsAll can only be used inside a LINQ expression.");

    /// <summary>CONTAINSANY — true if the array contains any of the specified values. Maps to <c>array::contains_any(arr, values)</c>.</summary>
    public static bool ContainsAny<T>(IEnumerable<T>? array, IReadOnlyList<T> values) => throw new NotSupportedException("SurrealArrayFunctions.ContainsAny can only be used inside a LINQ expression.");

    /// <summary>CONTAINSNONE — true if the array contains none of the specified values. Maps to <c>array::contains_none(arr, values)</c>.</summary>
    public static bool ContainsNone<T>(IEnumerable<T>? array, IReadOnlyList<T> values) => throw new NotSupportedException("SurrealArrayFunctions.ContainsNone can only be used inside a LINQ expression.");

    /// <summary>INTERSECTS — true if the array intersects with the specified values. Maps to <c>array::intersect(arr, values)</c>.</summary>
    public static bool Intersects<T>(IEnumerable<T>? array, IReadOnlyList<T> values) => throw new NotSupportedException("SurrealArrayFunctions.Intersects can only be used inside a LINQ expression.");
}
