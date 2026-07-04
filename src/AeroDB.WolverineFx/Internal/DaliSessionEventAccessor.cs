using System.Collections;
using System.Reflection;
using AeroDB;

namespace AeroDB.WolverineFx.Internal;

/// <summary>
/// Shared helper for accessing <c>DocumentSession._appendedEvents</c>
/// via reflection. Both <see cref="DaliEventForwarding"/> and
/// <see cref="FlushOutgoingMessagesOnDaliCommit"/> need this same logic.
/// Consolidating here avoids duplicating the reflection code.
///
/// A future AeroDB API (e.g., <c>DocumentSession.GetPendingAppendedEvents()</c>)
/// would eliminate the need for reflection entirely.
/// </summary>
internal static class DaliSessionEventAccessor
{
    private static readonly FieldInfo? AppendedEventsField;
    private static readonly FieldInfo? ValueTupleItem2Field;

    static DaliSessionEventAccessor()
    {
        AppendedEventsField = typeof(DocumentSession).GetField(
            "_appendedEvents", BindingFlags.NonPublic | BindingFlags.Instance);

        ValueTupleItem2Field = typeof(ValueTuple<string, object>).GetField(
            "Item2", BindingFlags.Public | BindingFlags.Instance);
    }

    /// <summary>
    /// Gets the list of appended event objects from a AeroDB DocumentSession
    /// by reading the internal <c>_appendedEvents</c> field.
    /// Returns an empty list if no events are pending or if reflection fails.
    /// </summary>
    public static List<object> GetAppendedEvents(DocumentSession session)
    {
        var list = AppendedEventsField?.GetValue(session) as IList;
        if (list is null || list.Count == 0)
            return [];

        var events = new List<object>(list.Count);
        foreach (var item in list)
        {
            if (ValueTupleItem2Field?.GetValue(item) is object evt)
                events.Add(evt);
        }
        return events;
    }
}
