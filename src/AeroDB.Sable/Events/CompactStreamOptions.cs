namespace AeroDB.Sable;

/// <summary>
/// Options for stream compaction. Controls whether an archived copy is retained
/// and how many recent events to keep (if any).
/// </summary>
public class CompactStreamOptions
{
    /// <summary>When true, the old events are archived to mt_archived_events before deletion.</summary>
    public bool KeepArchivedCopy { get; set; }

    /// <summary>If set, the most recent N events are retained (not deleted) after compaction.</summary>
    public int? KeepRecentEvents { get; set; }
}
