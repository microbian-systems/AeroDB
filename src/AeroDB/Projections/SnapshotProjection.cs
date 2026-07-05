namespace AeroDB;

/// <summary>
/// Self-aggregating projection. The aggregate type <typeparamref name="T"/> is also the projection.
/// Event handlers are convention-based: <c>Apply(EventType)</c> or <c>When(EventType)</c> methods
/// on the aggregate itself.
/// </summary>
public class SnapshotProjection<T> : EventProjection<T> where T : class, new()
{
    private readonly Type[] _eventTypes;

    /// <summary>
    /// Creates a new snapshot projection for the given aggregate type.
    /// Scans <typeparamref name="T"/> for <c>Apply</c> and <c>When</c> methods to determine
    /// which event types it handles.
    /// </summary>
    public SnapshotProjection(SnapshotOptions? options = null)
    {
        Options = options ?? new SnapshotOptions();
        // Scan T for all Apply/When methods to determine handled event types
        _eventTypes = typeof(T).GetMethods()
            .Where(m => (m.Name == "Apply" || m.Name == "When")
                && m.GetParameters().Length >= 1
                && !m.GetParameters()[0].ParameterType.IsPrimitive
                && m.GetParameters()[0].ParameterType != typeof(string))
            .Select(m => m.GetParameters()[0].ParameterType)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Configuration options for this snapshot projection.
    /// </summary>
    public SnapshotOptions Options { get; }

    /// <inheritdoc />
    public override Type[] EventTypes => _eventTypes;

    /// <inheritdoc />
    public override ProjectionLifecycle Lifecycle
    {
        get => Options.Lifecycle;
        set => Options.Lifecycle = value;
    }

    /// <inheritdoc />
    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        if (events.Count == 0)
            throw new InvalidOperationException("Cannot derive document ID from empty events.");
        var evt = events[0];
        if (evt is IEvent ievt)
            return ievt.StreamId;
        var prop = evt.GetType().GetProperty("StreamId");
        return prop?.GetValue(evt)?.ToString() ?? throw new InvalidOperationException("No StreamId on event.");
    }

    /// <summary>
    /// Applies events to the aggregate by invoking <c>Apply(EventType)</c> or <c>When(EventType)</c>
    /// methods on <typeparamref name="T"/> via reflection.
    /// When the source generator emits the dispatch override, this will use a compiled type-switch instead.
    /// </summary>
    protected override T? ApplyEvents(T? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        aggregate ??= new T();
        foreach (var evt in events)
        {
            if (evt is null) continue;
            var eventType = evt.GetType();
            var method = typeof(T).GetMethod("Apply", new[] { eventType })
                ?? typeof(T).GetMethod("When", new[] { eventType });
            if (method != null)
                method.Invoke(aggregate, new[] { evt });
        }
        return aggregate;
    }
}
