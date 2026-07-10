namespace AeroDB.Sable;

/// <summary>
/// Default implementation of <see cref="IProjectionContext"/>.
/// </summary>
public class ProjectionContext : IProjectionContext
{
    internal List<IProjectionSideEffect> SideEffects { get; } = new();

    public ProjectionContext(IDocumentOperations session, IEnumerable<IEvent> events)
    {
        Session = session as IDocumentSession
            ?? throw new ArgumentException("Session must implement IDocumentSession", nameof(session));
        Events = events.Select(e => (object?)e.Data).OfType<object>().ToList().AsReadOnly();
        TypedEvents = events.ToList().AsReadOnly();
    }

    public ProjectionContext(IDocumentSession session, IReadOnlyList<IEvent> typedEvents)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        TypedEvents = typedEvents ?? throw new ArgumentNullException(nameof(typedEvents));
        Events = typedEvents.Select(e => e.Data).ToList().AsReadOnly();
    }

    public IDocumentSession Session { get; }
    public IReadOnlyList<object> Events { get; }
    public IReadOnlyList<IEvent> TypedEvents { get; }

    public void RaiseSideEffect(IProjectionSideEffect sideEffect)
    {
        if (sideEffect is null) throw new ArgumentNullException(nameof(sideEffect));
        SideEffects.Add(sideEffect);
    }
}
