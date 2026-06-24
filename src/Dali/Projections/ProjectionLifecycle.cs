namespace Dali;

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
