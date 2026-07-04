using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroDB;

/// <summary>
/// Non-generic "do anything" projection. Subclasses override
/// <see cref="ApplyAsync(IDocumentOperations, IEvent, CancellationToken)"/>
/// to handle each event. Use the <c>operations</c> argument to store or delete
/// any document type.
/// 
/// Equivalent to Marten's non-generic <c>EventProjection</c>.
/// </summary>
public abstract class EventProjection : IProjection, ILoggableProjection
{
    private ILogger _logger = NullLogger.Instance;

    /// <inheritdoc />
    public virtual Type[] EventTypes => DiscoverEventTypes();

    /// <inheritdoc />
    public virtual ProjectionLifecycle Lifecycle { get; set; } = ProjectionLifecycle.Inline;

    /// <inheritdoc />
    public virtual string Name => GetType().Name;

    /// <summary>
    /// Override to handle each matching event. Use <paramref name="operations"/>
    /// to Store/Delete/Queue operations.
    /// </summary>
    public virtual Task ApplyAsync(
        IDocumentOperations operations,
        IEvent e,
        CancellationToken cancellation)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        foreach (var e in context.TypedEvents)
        {
            await ApplyAsync(context.Session, e, ct).ConfigureAwait(false);
            var transformed = InvokeTransform(e);
            if (transformed is not null)
            {
                context.Session.Store(transformed);
            }
        }
    }

    /// <inheritdoc />
    public virtual Task RebuildAsync(IDocumentSession session, CancellationToken ct)
    {
        _logger.LogWarning("RebuildAsync called on non-generic EventProjection {Name} — no-op by default", Name);
        return Task.CompletedTask;
    }

    /// <summary>Logger injection hook.</summary>
    void ILoggableProjection.SetLoggerFactory(ILoggerFactory? loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
    }

    private Type[]? _discoveredEventTypes;

    private Type[] DiscoverEventTypes()
    {
        if (_discoveredEventTypes is not null) return _discoveredEventTypes;

        _discoveredEventTypes = GetType()
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(m => m.Name is "Transform" or "Project")
            .Select(m => m.GetParameters().FirstOrDefault()?.ParameterType)
            .Where(t => t is not null)
            .Select(UnwrapEventType)
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct()
            .ToArray();

        return _discoveredEventTypes;
    }

    private object? InvokeTransform(IEvent e)
    {
        var eventType = e.EventType;
        var method = GetType()
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(m =>
            {
                if (m.Name != "Transform") return false;
                var parameters = m.GetParameters();
                if (parameters.Length != 1) return false;
                var parameterType = parameters[0].ParameterType;
                return parameterType.IsAssignableFrom(eventType)
                    || TryGetEventDataType(parameterType, out var dataType) && dataType == eventType;
            });

        if (method is null) return null;

        var parameterType = method.GetParameters()[0].ParameterType;
        var argument = parameterType.IsAssignableFrom(eventType)
            ? e.Data
            : BuildTypedEvent(parameterType, e);

        return method.Invoke(this, [argument]);
    }

    private static Type? UnwrapEventType(Type? parameterType)
    {
        if (parameterType is null) return null;
        return TryGetEventDataType(parameterType, out var dataType)
            ? dataType
            : parameterType;
    }

    private static bool TryGetEventDataType(Type parameterType, out Type dataType)
    {
        if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(IEvent<>))
        {
            dataType = parameterType.GetGenericArguments()[0];
            return true;
        }

        dataType = null!;
        return false;
    }

    private static object BuildTypedEvent(Type parameterType, IEvent e)
    {
        var dataType = parameterType.GetGenericArguments()[0];
        var envelopeType = typeof(Event<>).MakeGenericType(dataType);

        return Activator.CreateInstance(
            envelopeType,
            e.Data,
            e.Version,
            e.Sequence,
            e.Timestamp,
            e.StreamId.ToString(),
            e.StreamKey,
            e.Headers)!;
    }
}
