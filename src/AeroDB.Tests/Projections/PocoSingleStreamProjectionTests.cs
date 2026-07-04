namespace AeroDB.Tests.Projections;

public class TestTripProjection : SingleStreamProjection<PocoTrip>
{
    public static PocoTrip Create(TripStarted e) => new() { StartedOn = e.Day, Active = true };
    public PocoTrip Apply(TripEnded e, PocoTrip current)
    {
        current.Active = false;
        return current;
    }
    public PocoTrip Apply(Arrival e, PocoTrip current)
    {
        current.State = e.State;
        return current;
    }

    public override Type[] EventTypes => [typeof(TripStarted), typeof(TripEnded), typeof(Arrival)];
    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        // Derive a consistent document identity from event data.
        // PocoTrip uses Guid identity so return a valid GUID string.
        if (events.Count > 0)
        {
            var day = events[0] switch
            {
                TripStarted ts => ts.Day,
                TripEnded te => te.Day,
                Arrival a => a.Day,
                _ => 0
            };
            // Deterministic GUID from Day value (same Day → same GUID)
            var bytes = new byte[16];
            BitConverter.GetBytes(day).CopyTo(bytes, 0);
            return new Guid(bytes).ToString("D");
        }
        return Guid.NewGuid().ToString("D");
    }

    protected override PocoTrip? ApplyEvents(PocoTrip? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            if (aggregate is null)
            {
                if (e is TripStarted ts) aggregate = Create(ts);
            }
            else
            {
                if (e is TripEnded te) aggregate = Apply(te, aggregate);
                else if (e is Arrival a) aggregate = Apply(a, aggregate);
            }
        }
        return aggregate;
    }
}

public class PocoSingleStreamProjectionTests
{
    [Test]
    public async Task Creates_poco_document_from_events()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var streamId = Guid.NewGuid();
        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(streamId.ToString("D"), new[] { new TripStarted(5) });
        await session.Events.Append(streamId.ToString("D"), new[] { new Arrival(5, "Texas") });
        await session.SaveChangesAsync();

        // Session save should have triggered inline projection
        // Reload to verify
        var trips = await session.Query<PocoTrip>().ToListAsync();
        trips.ShouldNotBeEmpty();
        trips.ShouldContain(t => t.StartedOn == 5);
    }

    [Test]
    public async Task Updates_existing_poco_document()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var streamId = Guid.NewGuid();
        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(streamId.ToString("D"), new[] { new TripStarted(10) });
        await session.SaveChangesAsync();

        await session.Events.Append(streamId.ToString("D"), new[] { new TripEnded(10) });
        await session.SaveChangesAsync();

        var trips = await session.Query<PocoTrip>().ToListAsync();
        trips.ShouldNotBeEmpty();
        trips.ShouldContain(t => t.Active == false);
    }

    private static DocumentStore CreateStore()
    {
        return (DocumentStore)Documents.For(opts =>
        {
            opts.ClientFactory = () => new SurrealDbMemoryClient();
            opts.Namespace = "test";
            opts.Database = "test";
            opts.Schema.For<PocoTrip>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            ((List<IProjection>)opts.Projections).Add(new TestTripProjection());
        });
    }
}
