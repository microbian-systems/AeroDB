using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace AeroDB;

/// <summary>
/// Base class for AeroDB event subscriptions with built-in event type filtering.
/// Override <see cref="ProcessEventsAsync"/> to implement custom logic.
/// </summary>
public abstract class AeroDBSubscriptionBase : IAeroDBSubscription
{
    private readonly List<Type> _eventTypes = new();
    private Type? _streamType;
    private bool _includeArchivedEvents;

    /// <summary>
    /// Creates a subscription with the given name.
    /// The name is used to track processing progress.
    /// </summary>
    protected AeroDBSubscriptionBase(string subscriptionName)
    {
        SubscriptionName = subscriptionName ?? throw new ArgumentNullException(nameof(subscriptionName));
    }

    /// <inheritdoc />
    public string SubscriptionName { get; }

    /// <inheritdoc />
    public uint SubscriptionVersion { get; set; } = 1;

    /// <inheritdoc />
    public AsyncOptions Options { get; } = new();

    /// <summary>Include events of this type for processing.</summary>
    protected void IncludeType<T>()
    {
        _eventTypes.Add(typeof(T));
    }

    /// <summary>Include events of this type for processing.</summary>
    protected void IncludeType(Type type)
    {
        _eventTypes.Add(type);
    }

    /// <summary>Only process events from streams of the given aggregate type.</summary>
    protected void FilterIncomingEventsOnStreamType(Type streamType)
    {
        _streamType = streamType;
    }

    /// <summary>Whether to include archived events (default: false).</summary>
    protected bool IncludeArchivedEvents
    {
        get => _includeArchivedEvents;
        set => _includeArchivedEvents = value;
    }

    /// <inheritdoc />
    void IAeroDBSubscription.Filter(IEventFilterable filterable)
    {
        foreach (var eventType in _eventTypes)
            filterable.IncludeType(eventType);

        if (_streamType != null)
            filterable.FilterIncomingEventsOnStreamType(_streamType);

        filterable.IncludeArchivedEvents = _includeArchivedEvents;
    }

    /// <inheritdoc />
    public abstract Task ProcessEventsAsync(
        EventRange page,
        ISubscriptionController controller,
        IDocumentSession session,
        CancellationToken cancellationToken);
}
