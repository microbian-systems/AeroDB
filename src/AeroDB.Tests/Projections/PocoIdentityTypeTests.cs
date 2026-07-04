namespace AeroDB.Tests.Projections;

public class LongIdentityPoco { public long Id { get; set; } public string Name { get; set; } = ""; }
public class StringIdentityPoco { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
public record LongIdEvent(long Id, string Name);

public class LongIdentityProjection : SingleStreamProjection<LongIdentityPoco>
{
    public static LongIdentityPoco Create(LongIdEvent e) => new() { Id = e.Id, Name = e.Name };
    public override Type[] EventTypes => [typeof(LongIdEvent)];
    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        if (events.FirstOrDefault() is LongIdEvent e) return e.Id;
        return 0L;
    }

    protected override LongIdentityPoco? ApplyEvents(LongIdentityPoco? aggregate, IReadOnlyList<object> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            if (aggregate is null)
            {
                if (e is LongIdEvent le) aggregate = Create(le);
            }
        }
        return aggregate;
    }
}

public class PocoIdentityTypeTests
{
    [Test]
    public async Task Long_identity_poco_works()
    {
        var store = (DocumentStore)Documents.For(opts =>
        {
            opts.ClientFactory = () => new SurrealDbMemoryClient();
            opts.Namespace = "test";
            opts.Database = "test";
            opts.Schema.For<LongIdentityPoco>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            ((List<IProjection>)opts.Projections).Add(new LongIdentityProjection());
        });
        await store.InitializeAsync();

        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(Guid.NewGuid().ToString("D"), new[] { new LongIdEvent(999, "hi") });
        await session.SaveChangesAsync();

        var loaded = await session.LoadAsync<LongIdentityPoco>("999");
        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(999);
        loaded.Name.ShouldBe("hi");
    }

    [Test]
    public async Task Guid_identity_poco_works()
    {
        var id = Guid.NewGuid();
        var store = (DocumentStore)Documents.For(opts =>
        {
            opts.ClientFactory = () => new SurrealDbMemoryClient();
            opts.Namespace = "test";
            opts.Database = "test";
            opts.Schema.For<PocoTrip>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            ((List<IProjection>)opts.Projections).Add(new TestTripProjection());
        });
        await store.InitializeAsync();

        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(id.ToString("D"), new[] { new TripStarted(3) });
        await session.SaveChangesAsync();

        var trips = await session.Query<PocoTrip>().ToListAsync();
        trips.Count.ShouldBe(1);
    }
}
