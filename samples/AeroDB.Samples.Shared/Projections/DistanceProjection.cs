using AeroDB;
using AeroDB.Sable;

namespace AeroDB.Samples.Shared;

public partial class DistanceProjection : EventProjection<Distance>
{
    public DistanceProjection()
    {
    }

    public override ProjectionLifecycle Lifecycle => ProjectionLifecycle.Async;

    public Distance Create(Travel travel, IEvent e)
    {
        return new Distance { Id = e.StreamKey, Day = travel.Day, Total = travel.TotalDistance() };
    }

    public override Type[] EventTypes => new[] { typeof(Travel) };

    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        foreach (var e in events)
        {
            if (e is IEvent ie) return ie.StreamKey;
        }
        return Guid.NewGuid();
    }
}
