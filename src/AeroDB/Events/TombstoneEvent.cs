namespace AeroDB;

/// <summary>
/// Marker event for gap-filling in event streams. Written by <c>WriteTombstone</c>.
/// </summary>
public class TombstoneEvent
{
    public string StreamId { get; set; } = "";
    public long Version { get; set; }
    public string Reason { get; set; } = "";
}
