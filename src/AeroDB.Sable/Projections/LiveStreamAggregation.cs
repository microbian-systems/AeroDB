using System.Reflection;

namespace AeroDB.Sable;

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
        var aggregateType = typeof(T);
        T? aggregate = null;

        foreach (var e in events)
        {
            if (e.Data is null) continue;
            var eventType = e.Data.GetType();
            if (aggregate is null)
            {
                var create = aggregateType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m =>
                        m.Name == "Create"
                        && aggregateType.IsAssignableFrom(m.ReturnType)
                        && m.GetParameters() is [{ } p]
                        && p.ParameterType.IsAssignableFrom(eventType));

                aggregate = (T?)create?.Invoke(null, [e.Data]);
                if (aggregate is not null) continue;

                aggregate = Activator.CreateInstance<T>();
            }

            var method = aggregateType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m =>
                    m.Name is "Apply" or "When"
                    && m.GetParameters() is [{ } p]
                    && p.ParameterType.IsAssignableFrom(eventType));

            var result = method?.Invoke(aggregate, [e.Data]);
            if (result is T typed)
            {
                aggregate = typed;
            }
        }

        return aggregate ?? Activator.CreateInstance<T>();
    }
}
