namespace Dali;

/// <summary>
/// Default implementation of <see cref="IProjectionContext"/>.
/// </summary>
public class ProjectionContext : IProjectionContext
{
    public ProjectionContext(IDocumentSession session, IReadOnlyList<IEvent> typedEvents)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        TypedEvents = typedEvents ?? throw new ArgumentNullException(nameof(typedEvents));
        Events = typedEvents.Select(e => e.Data).ToList().AsReadOnly();
    }

    public IDocumentSession Session { get; }
    public IReadOnlyList<object> Events { get; }
    public IReadOnlyList<IEvent> TypedEvents { get; }
}
