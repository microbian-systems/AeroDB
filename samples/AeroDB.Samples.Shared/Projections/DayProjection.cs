using AeroDB;
using AeroDB.Sable;

namespace AeroDB.Samples.Shared;

public partial class DayProjection : EventProjection<Day>
{
    public DayProjection()
    {
    }

    public override ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;

    public void Apply(Day day, TripStarted e) => day.Started++;
    public void Apply(Day day, TripEnded e) => day.Ended++;

    public void Apply(Day day, Movement e)
    {
        switch (e.Direction)
        {
            case Direction.East: day.East += e.Distance; break;
            case Direction.North: day.North += e.Distance; break;
            case Direction.South: day.South += e.Distance; break;
            case Direction.West: day.West += e.Distance; break;
        }
    }

    public void Apply(Day day, Stop e) => day.Stops++;

    public override Type[] EventTypes => new[]
    {
        typeof(TripStarted), typeof(TripEnded), typeof(Movement), typeof(Stop)
    };

    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        foreach (var e in events)
        {
            if (e is IDayEvent de) return de.Day;
        }
        return 0;
    }
}
