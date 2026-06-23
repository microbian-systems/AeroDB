using Microsoft.Extensions.Logging;
using SurrealDb.Net.Models;

namespace Dali;

/// <summary>
/// Projection base class with per-event-type dispatch. Subclasses declare
/// <c>Create(EventType, IEvent&lt;EventType&gt;, CancellationToken)</c>,
/// <c>Apply(EventType, T, IEvent&lt;EventType&gt;, CancellationToken)</c>, and
/// <c>ShouldDelete(EventType, T, IEvent&lt;EventType&gt;, CancellationToken)</c>
/// methods. Dispatch is handled by the source generator or a runtime reflection fallback.
/// </summary>
/// <typeparam name="T">The projected document type (must extend <see cref="SurrealDb.Net.Models.Record"/>).</typeparam>
public abstract partial class EventProjection<T> : InlineProjection<T> where T : Record
{
    /// <summary>
    /// Auto-generated dispatch switch. If the source generator hasn't run (e.g., IDE design-time),
    /// falls back to runtime reflection-based dispatch with a warning log.
    /// </summary>
    protected override T? ApplyEvents(T? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        if (events.Count == 0) return aggregate;

        // Source-gen overrides this in partial class. If not overridden, use reflection fallback.
        return ApplyEventsReflection(aggregate, events, ct);
    }

    /// <summary>
    /// Runtime reflection fallback for event dispatch. Logs a warning since this is slower.
    /// </summary>
    private T? ApplyEventsReflection(T? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        Logger.LogWarning(
            "EventProjection<{Type}> is using runtime reflection dispatch. " +
            "Source generator should have emitted a partial class override. " +
            "Check that Dali.SourceGenerators is referenced and the projection class is 'partial'.",
            typeof(T).Name);

        var aggregateType = typeof(T);
        foreach (var evt in events)
        {
            if (evt is null) continue;
            var eventType = evt.GetType();

            if (aggregate is null)
            {
                // Try Create(EventType, IEvent<EventType>, CancellationToken)
                var createMethod = GetType().GetMethod("Create",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { eventType, typeof(IEvent<>).MakeGenericType(eventType), typeof(CancellationToken) },
                    null);
                if (createMethod != null)
                {
                    // We need an IEvent wrapper for Create. Construct one.
                    var ieventType = typeof(Event<>).MakeGenericType(eventType);
                    var ievent = Activator.CreateInstance(ieventType, evt, 0L, 0L, DateTimeOffset.MinValue, "", Guid.Empty);
                    aggregate = (T?)createMethod.Invoke(this, new[] { evt, ievent, ct });
                }
                else
                {
                    // Fallback: Create with just event data and CancellationToken
                    createMethod = GetType().GetMethod("Create",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, new[] { eventType, typeof(CancellationToken) },
                        null);
                    if (createMethod != null)
                        aggregate = (T?)createMethod.Invoke(this, new[] { evt, ct });
                }
            }
            else
            {
                // Try ShouldDelete(EventType, T, IEvent<EventType>, CancellationToken)
                var deleteMethod = GetType().GetMethod("ShouldDelete",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { eventType, aggregateType, typeof(IEvent<>).MakeGenericType(eventType), typeof(CancellationToken) },
                    null);
                if (deleteMethod != null)
                {
                    var ieventType = typeof(Event<>).MakeGenericType(eventType);
                    var ievent = Activator.CreateInstance(ieventType, evt, 0L, 0L, DateTimeOffset.MinValue, "", Guid.Empty);
                    var result = deleteMethod.Invoke(this, new[] { evt, aggregate, ievent, ct });
                    if (result is null) return null; // ShouldDelete returned null → delete
                    if (result is bool b && !b) return null; // ShouldDelete returned false → delete
                }
                else
                {
                    // Fallback: ShouldDelete(EventType, T, CancellationToken)
                    deleteMethod = GetType().GetMethod("ShouldDelete",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, new[] { eventType, aggregateType, typeof(CancellationToken) },
                        null);
                    if (deleteMethod != null)
                    {
                        var result = deleteMethod.Invoke(this, new[] { evt, aggregate, ct });
                        if (result is null) return null;
                        if (result is bool b && !b) return null;
                    }
                }

                // Try Apply(EventType, T, IEvent<EventType>, CancellationToken)
                var applyMethod = GetType().GetMethod("Apply",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { eventType, aggregateType, typeof(IEvent<>).MakeGenericType(eventType), typeof(CancellationToken) },
                    null);
                if (applyMethod != null)
                {
                    var ieventType = typeof(Event<>).MakeGenericType(eventType);
                    var ievent = Activator.CreateInstance(ieventType, evt, 0L, 0L, DateTimeOffset.MinValue, "", Guid.Empty);
                    aggregate = (T?)applyMethod.Invoke(this, new[] { evt, aggregate, ievent, ct });
                }
                else
                {
                    // Fallback: Apply(EventType, T, CancellationToken)
                    applyMethod = GetType().GetMethod("Apply",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, new[] { eventType, aggregateType, typeof(CancellationToken) },
                        null);
                    if (applyMethod != null)
                        aggregate = (T?)applyMethod.Invoke(this, new[] { evt, aggregate, ct });
                }
            }
        }
        return aggregate;
    }
}
