using System.Reflection;

namespace Dali;

/// <summary>
/// Aggregates events from a stream into a result using convention-based Apply methods.
/// Unlike <see cref="SnapshotProjection{T}"/>, this does not store the result — it is purely read-side.
/// </summary>
public static class LiveStreamAggregation
{
    public static async Task<T?> AggregateAsync<T>(
        this IEvents events, string streamId, CancellationToken ct = default)
        where T : class
    {
        var streamEvents = await events.FetchStream(streamId, ct).ConfigureAwait(false);
        if (streamEvents.Count == 0) return Activator.CreateInstance<T>();

        return AggregateEvents<T>(streamEvents);
    }

    /// <summary>Guid variant.</summary>
    public static async Task<T?> AggregateAsync<T>(
        this IEvents events, Guid streamId, CancellationToken ct = default)
        where T : class
    {
        return await AggregateAsync<T>(events, streamId.ToString("D"), ct).ConfigureAwait(false);
    }

    internal static T AggregateEvents<T>(IReadOnlyList<IEvent> events) where T : class
    {
        var aggregate = Activator.CreateInstance<T>();
        var aggregateType = typeof(T);

        foreach (var e in events)
        {
            if (e.Data is null) continue;
            var eventType = e.Data.GetType();
            var method = aggregateType.GetMethod("Apply", new[] { eventType })
                ?? aggregateType.GetMethod("When", new[] { eventType });
            method?.Invoke(aggregate, new[] { e.Data });
        }

        return aggregate;
    }
}
