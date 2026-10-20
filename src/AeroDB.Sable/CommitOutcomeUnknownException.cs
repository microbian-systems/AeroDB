namespace AeroDB.Sable;

/// <summary>
/// Commit was submitted, but its acknowledgement was not received. The database may
/// have committed the operation. Reconcile using a stable operation identifier before
/// retrying; this exception is deliberately a fault, not a canceled task.
/// </summary>
public sealed class CommitOutcomeUnknownException(Exception innerException)
    : Exception("The database commit outcome is unknown. Reconcile the operation before retrying.", innerException);
