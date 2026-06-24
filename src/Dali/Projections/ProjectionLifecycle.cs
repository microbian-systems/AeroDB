namespace Dali;

/// <summary>Defines when a projection is applied. <c>Inline</c> projections run synchronously during <c>SaveChanges</c>; <c>Async</c> projections are processed by the background daemon; <c>Live</c> projections are computed on-demand at read time.</summary>
public enum ProjectionLifecycle
{
    /// Applied synchronously within SaveChangesAsync
    Inline,

    /// Applied by AsyncDaemon in background
    Async,

    /// Computed on-the-fly by replaying stream events.
    /// No projection data is stored — reads use <see cref="LiveStreamAggregation"/>.
    /// The daemon ignores projections with this lifecycle.
    Live
}
