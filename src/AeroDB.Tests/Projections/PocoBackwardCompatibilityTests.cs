namespace AeroDB.Tests.Projections;

public class RecordTripProjection : SingleStreamProjection<RecordTrip>
{
    public static RecordTrip Create(TripStarted e) => new() { StartedOn = e.Day, Active = true };
    public RecordTrip Apply(Arrival e, RecordTrip current)
    {
        current.State = e.State;
        return current;
    }

    public override Type[] EventTypes => [typeof(TripStarted), typeof(Arrival)];
    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        if (events.Count > 0)
        {
            return events[0] switch
            {
                TripStarted ts => $"trip_{ts.Day}",
                Arrival a => $"trip_{a.Day}",
                _ => Guid.NewGuid().ToString("D")
            };
        }
        return Guid.NewGuid().ToString("D");
    }

    protected override RecordTrip? ApplyEvents(RecordTrip? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            if (aggregate is null)
            {
                if (e is TripStarted ts) aggregate = Create(ts);
            }
            else
            {
                if (e is Arrival a) aggregate = Apply(a, aggregate);
            }
        }
        return aggregate;
    }
}

public class PocoBackwardCompatibilityTests
{
    [Test]
    public async Task Record_subclass_projections_still_work()
    {
        var store = (DocumentStore)Documents.For(opts =>
        {
            opts.ClientFactory = () => new SurrealDbMemoryClient();
            opts.Namespace = "test";
            opts.Database = "test";
            opts.Projections.Add(new RecordTripProjection());
        });
        await store.InitializeAsync();

        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(Guid.NewGuid().ToString("D"), new[] { new TripStarted(1) });
        await session.Events.StartStream(Guid.NewGuid().ToString("D"), new[] { new Arrival(1, "Kansas") }); // Note: different stream
        await session.SaveChangesAsync();

        var trips = await session.Query<RecordTrip>().ToListAsync();
        trips.ShouldNotBeEmpty();
        trips.ShouldContain(t => t.StartedOn == 1);
    }

    [Test]
    public async Task Poco_and_record_projections_coexist()
    {
        var store = (DocumentStore)Documents.For(opts =>
        {
            opts.ClientFactory = () => new SurrealDbMemoryClient();
            opts.Namespace = "test";
            opts.Database = "test";
            opts.Schema.For<PocoTrip>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            opts.Projections.Add(new TestTripProjection());
            opts.Projections.Add(new RecordTripProjection());
        });
        await store.InitializeAsync();

        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(Guid.NewGuid().ToString("D"), new[] { new TripStarted(7) });
        await session.SaveChangesAsync();

        var pocoTrips = await session.Query<PocoTrip>().ToListAsync();
        var recordTrips = await session.Query<RecordTrip>().ToListAsync();
        pocoTrips.ShouldNotBeEmpty();
        recordTrips.ShouldNotBeEmpty();
    }
}
