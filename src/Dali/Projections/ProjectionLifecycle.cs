namespace Dali;

public enum ProjectionLifecycle
{
    /// Applied synchronously within SaveChangesAsync
    Inline,

    /// Applied by AsyncDaemon in background
    Async
}
