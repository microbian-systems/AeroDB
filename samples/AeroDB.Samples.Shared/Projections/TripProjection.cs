using AeroDB;

namespace AeroDB.Samples.Shared;

public partial class TripProjection : EventProjection<Trip>
{
    public TripProjection()
    {
    }

    public override ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;

    public void Apply(Arrival e, Trip trip) => trip.State = e.State;

    public void Apply(Travel e, Trip trip)
    {
        trip.Traveled += e.TotalDistance();
    }

    public void Apply(TripEnded e, Trip trip)
    {
        trip.Active = false;
        trip.EndedOn = e.Day;
    }

    public Trip Create(IEvent<TripStarted> started)
    {
        var streamId = started.StreamKey;
        return new Trip { Id = streamId, StartedOn = started.Data.Day, Active = true };
    }

    public bool ShouldDelete(TripAborted _) => true;
    public bool ShouldDelete(Breakdown e) => e.IsCritical;
    public bool ShouldDelete(VacationOver _, Trip trip) => trip.Traveled > 1000;

    public override Type[] EventTypes => new[]
    {
        typeof(TripStarted), typeof(Travel), typeof(TripEnded),
        typeof(Arrival), typeof(TripAborted), typeof(Breakdown), typeof(VacationOver)
    };

    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        // SingleStreamProjection — document ID is stream ID
        foreach (var e in events)
        {
            if (e is IEvent ie)
                return ie.StreamKey;
        }
        return Guid.NewGuid();
    }
}

public partial class TripProjectionWithCustomName : TripProjection
{
    public TripProjectionWithCustomName()
    {
    }

    public override ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;
}
