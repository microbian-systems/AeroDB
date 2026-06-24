namespace Dali;

/// <summary>Marker interface for side effects raised during projection processing.</summary>
public interface IProjectionSideEffect { }

/// <summary>Side effect: append an event to a stream.</summary>
public sealed record AppendEventSideEffect(string StreamId, object Event) : IProjectionSideEffect;

/// <summary>Maximum side-effect processing depth to prevent infinite loops.</summary>
public class ProjectionReentrancyException : InvalidOperationException
{
    public string? StreamId { get; }
    public ProjectionReentrancyException(string streamId, int depth)
        : base($"Projection reentrancy limit reached for stream '{streamId}' at depth {depth}.")
    {
        StreamId = streamId;
    }
}
