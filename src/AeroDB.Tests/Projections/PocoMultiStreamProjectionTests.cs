namespace AeroDB.Tests.Projections;

public class TestDayProjection : MultiStreamProjection<PocoDay>
{
    public static PocoDay Create(TripStarted e) => new() { Id = e.Day, Started = 1 };
    public PocoDay Apply(TripStarted e, PocoDay current)
    {
        current.Started += 1;
        return current;
    }
    public PocoDay Apply(TripEnded e, PocoDay current)
    {
        current.Ended += 1;
        return current;
    }

    public override Type[] EventTypes => [typeof(TripStarted), typeof(TripEnded)];
    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        foreach (var e in events)
        {
            if (e is TripStarted ts) return ts.Day;
            if (e is TripEnded te) return te.Day;
        }
        return 0;
    }

    protected override PocoDay? ApplyEvents(PocoDay? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            if (aggregate is null)
            {
                if (e is TripStarted ts) aggregate = Create(ts);
            }
            else
            {
                if (e is TripStarted ts) aggregate = Apply(ts, aggregate);
                else if (e is TripEnded te) aggregate = Apply(te, aggregate);
            }
        }
        return aggregate;
    }
}

public class PocoMultiStreamProjectionTests
{
    [Test]
    public async Task Creates_poco_document_from_cross_stream_events()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(Guid.NewGuid().ToString("D"), new[] { new TripStarted(42) });
        await session.Events.StartStream(Guid.NewGuid().ToString("D"), new[] { new TripStarted(42) });
        await session.SaveChangesAsync();

        var days = await session.Query<PocoDay>().ToListAsync();
        days.ShouldNotBeEmpty();
        days.ShouldContain(d => d.Id == 42 && d.Started >= 1);
    }

    private static DocumentStore CreateStore()
    {
        return (DocumentStore)Documents.For(opts =>
        {
            opts.ClientFactory = () => new SurrealDbMemoryClient();
            opts.Namespace = "test";
            opts.Database = "test";
            opts.Schema.For<PocoDay>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            opts.Projections.Add(new TestDayProjection());
        });
    }
}
