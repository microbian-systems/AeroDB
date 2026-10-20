namespace AeroDB.Sable;

/// <summary>
/// The database commit succeeded, but an AfterCommit callback failed or was
/// canceled. Do not retry the database write. Reconcile post-commit processing
/// separately: earlier callbacks may already have produced effects.
/// </summary>
/// <remarks>
/// The original callback cause is retained for trusted callers. It may contain
/// sensitive data and must not be logged or returned without boundary redaction.
/// Cancellation here is a fault with a known successful commit, not cancellation
/// of the database operation or an unknown database outcome.
/// </remarks>
public sealed class PostCommitCallbackException(Exception innerException)
    : InvalidOperationException("The database commit succeeded, but post-commit processing failed. Do not retry the database write.", innerException)
{
    /// <summary>The database acknowledged the commit before callbacks ran.</summary>
    public bool DatabaseCommitSucceeded => true;
}
