namespace AeroDB.Tests.Projections;

public class TestDistanceProjection : EventProjection<PocoDistance>
{
    public static PocoDistance Create(Travel e) => new() { Day = e.Day, Total = e.Distance };
    public PocoDistance Apply(Travel e, PocoDistance current)
    {
        current.Total += e.Distance;
        return current;
    }

    public override Type[] EventTypes => [typeof(Travel)];
    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        if (events.FirstOrDefault() is Travel t) return t.Day;
        return 0;
    }

    protected override PocoDistance? ApplyEvents(PocoDistance? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            if (aggregate is null)
            {
                if (e is Travel t) aggregate = Create(t);
            }
            else
            {
                if (e is Travel t) aggregate = Apply(t, aggregate);
            }
        }
        return aggregate;
    }
}

public class PocoEventProjectionTests
{
    [Test]
    public async Task Creates_poco_from_single_event()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(Guid.NewGuid().ToString("D"), new[] { new Travel(99, 150.0) });
        await session.SaveChangesAsync();

        var distances = await session.Query<PocoDistance>().ToListAsync();
        distances.ShouldNotBeEmpty();
        distances.ShouldContain(d => d.Day == 99 && d.Total == 150.0);
    }

    [Test]
    public async Task Accumulates_poco_from_multiple_events()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var streamId = Guid.NewGuid();
        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(streamId.ToString("D"), new[] { new Travel(100, 50.0) });
        await session.SaveChangesAsync();
        await session.Events.Append(streamId.ToString("D"), new[] { new Travel(100, 30.0) });
        await session.SaveChangesAsync();

        var distances = await session.Query<PocoDistance>().ToListAsync();
        distances.ShouldContain(d => d.Total >= 50.0);
    }

    private static DocumentStore CreateStore()
    {
        return (DocumentStore)Documents.For(opts =>
        {
            opts.ClientFactory = () => new SurrealDbMemoryClient();
            opts.Namespace = "test";
            opts.Database = "test";
            opts.Schema.For<PocoDistance>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            ((List<IProjection>)opts.Projections).Add(new TestDistanceProjection());
        });
    }
}
