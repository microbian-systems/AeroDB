using System.Reflection;

namespace AeroDB;

/// <summary>
/// Single-stream projection with a typed stream identity.
/// Supports both Marten-style static Create/Apply convention methods
/// and AeroDB's instance-based Create/Apply/ShouldDelete methods.
/// </summary>
/// <typeparam name="TDoc">Projected document type.</typeparam>
/// <typeparam name="TId">Stream identity type (Guid, string, int, long).</typeparam>
public abstract class SingleStreamProjection<TDoc, TId> : InlineProjection<TDoc>
    where TDoc : class
{
    /// <summary>
    /// Extracts the stream identity as a typed <typeparamref name="TId"/> from the events.
    /// Default: uses <see cref="IEvent.StreamKey"/> for Guid, <see cref="IEvent.StreamId"/> for string,
    /// or parses <see cref="IEvent.StreamId"/> for numeric types.
    /// Override for custom identity derivation.
    /// </summary>
    protected virtual TId GetDocumentIdTyped(IReadOnlyList<object> events)
    {
        if (events.Count == 0)
            throw new InvalidOperationException("Cannot derive document ID from empty events.");

        if (events[0] is IEvent ievt)
        {
            if (typeof(TId) == typeof(Guid))
                return (TId)(object)ievt.StreamKey;
            if (typeof(TId) == typeof(string))
                return (TId)(object)ievt.StreamId.ToString();
            return (TId)Convert.ChangeType(ievt.StreamId.ToString(), typeof(TId));
        }

        var evt = events[0];
        var streamProp = evt.GetType().GetProperty("StreamId");
        if (streamProp is null)
            throw new InvalidOperationException($"Event type {evt.GetType().Name} lacks StreamId.");

        var raw = streamProp.GetValue(evt);
        if (raw is TId tid) return tid;
        return (TId)Convert.ChangeType(raw?.ToString() ?? "", typeof(TId));
    }

    /// <inheritdoc />
    protected override object GetDocumentId(IReadOnlyList<object> events)
        => GetDocumentIdTyped(events)!;

    /// <summary>
    /// Apply events to the aggregate. Searches for static Create(EventType) and
    /// static Apply(EventType, TDoc) Marten conventions, falling through to the
    /// base InlineProjection.ApplyEvents for AeroDB instance conventions.
    /// </summary>
    protected override TDoc? ApplyEvents(TDoc? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        if (events.Count == 0) return aggregate;

        foreach (var evt in events)
        {
            if (evt is null) continue;
            var eventType = evt.GetType();

            if (aggregate is null)
            {
                var created = TryStaticCreate(eventType, evt)
                    ?? TryStaticCreateWithIEvent(eventType, evt)
                    ?? TryInstanceCreate(eventType, evt, ct);
                if (created is not null)
                    aggregate = created;
            }
            else
            {
                var result = TryStaticApply(eventType, evt, aggregate)
                    ?? TryInstanceApply(eventType, aggregate, evt, ct);
                if (result is not null)
                    aggregate = result;
            }
        }
        return aggregate;
    }

    private TDoc? TryStaticCreate(Type eventType, object evt)
    {
        var method = GetType().GetMethod("Create",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { eventType }, null);
        return (TDoc?)method?.Invoke(null, new[] { evt });
    }

    private TDoc? TryStaticCreateWithIEvent(Type eventType, object evt)
    {
        var iEventType = typeof(IEvent<>).MakeGenericType(eventType);
        var method = GetType().GetMethod("Create",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { iEventType }, null);
        if (method is null) return null;
        var ievent = WrapEvent(evt, eventType);
        return (TDoc?)method.Invoke(null, new[] { ievent });
    }

    private TDoc? TryInstanceCreate(Type eventType, object evt, CancellationToken ct)
    {
        var method = GetType().GetMethod("Create",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { eventType, typeof(IEvent<>).MakeGenericType(eventType), typeof(CancellationToken) }, null)
            ?? GetType().GetMethod("Create",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { eventType, typeof(CancellationToken) }, null);
        if (method is null) return null;
        var parameters = method.GetParameters();
        if (parameters.Length >= 2 && parameters[1].ParameterType.IsGenericType
            && parameters[1].ParameterType.GetGenericTypeDefinition() == typeof(IEvent<>))
        {
            var ievent = WrapEvent(evt, eventType);
            return parameters.Length == 3
                ? (TDoc?)method.Invoke(this, new[] { evt, ievent, ct })
                : (TDoc?)method.Invoke(this, new[] { evt, ievent });
        }
        return (TDoc?)method.Invoke(this, new[] { evt, ct });
    }

    private TDoc? TryStaticApply(Type eventType, object evt, TDoc aggregate)
    {
        var method = GetType().GetMethod("Apply",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { eventType, typeof(TDoc) }, null);
        return method is not null
            ? (TDoc?)method.Invoke(null, new[] { evt, aggregate })
            : null;
    }

    private TDoc? TryInstanceApply(Type eventType, TDoc aggregate, object evt, CancellationToken ct)
    {
        var method = GetType().GetMethod("Apply",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { eventType, typeof(TDoc), typeof(IEvent<>).MakeGenericType(eventType), typeof(CancellationToken) }, null)
            ?? GetType().GetMethod("Apply",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { eventType, typeof(TDoc), typeof(CancellationToken) }, null)
            ?? GetType().GetMethod("Apply",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { eventType, typeof(TDoc) }, null);
        if (method is null) return aggregate;

        var parameters = method.GetParameters();
        if (parameters.Length >= 3 && parameters[2].ParameterType.IsGenericType
            && parameters[2].ParameterType.GetGenericTypeDefinition() == typeof(IEvent<>))
        {
            var ievent = WrapEvent(evt, eventType);
            return parameters.Length == 4
                ? (TDoc?)method.Invoke(this, new[] { evt, aggregate, ievent, ct })
                : (TDoc?)method.Invoke(this, new[] { evt, aggregate, ievent });
        }
        return (TDoc?)method.Invoke(this, new[] { evt, aggregate, ct });
    }

    private static object WrapEvent(object evt, Type eventType)
    {
        var genericType = typeof(Event<>).MakeGenericType(eventType);
        return Activator.CreateInstance(genericType, evt, 0L, 0L, DateTimeOffset.MinValue, "", Guid.Empty)
            ?? throw new InvalidOperationException($"Failed to create Event<{eventType.Name}> wrapper.");
    }
}
