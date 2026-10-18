namespace AeroDB.Sable;

/// <summary>
/// A managed immediate mutation was submitted, but its outcome could not be
/// confirmed. The mutation may have been applied. Reconcile before retrying.
/// This is deliberately a fault rather than a canceled task.
/// </summary>
/// <param name="innerException">The post-dispatch uncertainty cause.</param>
/// <param name="callerCancellationToken">The token supplied by the caller to the mutation.</param>
public sealed class ImmediateMutationOutcomeUnknownException(
    Exception innerException,
    CancellationToken callerCancellationToken)
    : Exception(
        "The immediate database mutation outcome is unknown. Reconcile the operation before retrying.",
        innerException)
{
    /// <summary>The original token supplied by the caller to the mutation.</summary>
    public CancellationToken CallerCancellationToken { get; } = callerCancellationToken;
}
