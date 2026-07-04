namespace AeroDB;

/// <summary>
/// Per-session concurrency detection mode. When set to <see cref="Disabled"/>,
/// optimistic concurrency checks are skipped even if the store has
/// <c>UseOptimisticConcurrency = true</c>.
/// </summary>
public enum ConcurrencyChecks
{
    /// <summary>Disable optimistic concurrency checks for this session.</summary>
    Disabled,

    /// <summary>Enable optimistic concurrency checks for this session (store default).</summary>
    Enabled
}
