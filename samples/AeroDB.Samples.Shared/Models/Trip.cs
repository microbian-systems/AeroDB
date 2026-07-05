namespace AeroDB.Samples.Shared;

public class Trip
{
    public Guid Id { get; set; }
    public int EndedOn { get; set; }
    public double Traveled { get; set; }
    public string State { get; set; } = "";
    public bool Active { get; set; }
    public int StartedOn { get; set; }
    public Guid? RepairShopId { get; set; }

    // Live aggregation methods (for self-aggregating Snapshot)
    internal void Apply(Arrival e) => State = e.State;
    internal void Apply(Travel e) => Traveled += e.TotalDistance();
    internal void Apply(TripEnded e) { Active = false; EndedOn = e.Day; }

    internal bool ShouldDelete(TripAborted e) => true;
    internal bool ShouldDelete(Breakdown e) => e.IsCritical;
    internal bool ShouldDelete(VacationOver e) => Traveled > 1000;

    public override string ToString() =>
        $"{nameof(Id)}: {Id}, {nameof(EndedOn)}: {EndedOn}, {nameof(Traveled)}: {Traveled}, {nameof(State)}: {State}, {nameof(Active)}: {Active}, {nameof(StartedOn)}: {StartedOn}";
}
