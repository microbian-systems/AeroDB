namespace Dali;

/// <summary>
/// Default implementation of <see cref="IProjectionContext"/>.
/// </summary>
public class ProjectionContext : IProjectionContext
{
    public ProjectionContext(IDocumentSession session, IReadOnlyList<object> events)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public IDocumentSession Session { get; }
    public IReadOnlyList<object> Events { get; }
}
