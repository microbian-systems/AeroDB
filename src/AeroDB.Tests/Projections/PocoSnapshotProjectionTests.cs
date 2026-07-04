namespace AeroDB.Tests.Projections;

public class PocoSnapshotProjectionTests
{
    [Test]
    public async Task Self_aggregates_poco_via_apply_methods()
    {
        var projection = new SnapshotProjection<PocoSnapshot>(new SnapshotOptions
        {
            Lifecycle = ProjectionLifecycle.Live
        });

        var store = CreateStore(projection);
        await store.InitializeAsync();

        var streamId = Guid.NewGuid();
        await using var session = await store.LightweightSessionAsync();
        await session.Events.StartStream(streamId.ToString("D"), new[] { new PocoSnapshotIncremented(5) });
        await session.Events.Append(streamId.ToString("D"), new[] { new PocoSnapshotIncremented(3) });
        await session.Events.Append(streamId.ToString("D"), new[] { new PocoSnapshotRenamed("Test") });
        await session.SaveChangesAsync();

        var snapshot = await session.Events.AggregateStreamAsync<PocoSnapshot>(streamId.ToString("D"));
        snapshot.ShouldNotBeNull();
        snapshot.Count.ShouldBe(8); // 5 + 3
        snapshot.Name.ShouldBe("Test");
    }

    private static DocumentStore CreateStore(SnapshotProjection<PocoSnapshot> projection)
    {
        return (DocumentStore)Documents.For(opts =>
        {
            opts.ClientFactory = () => new SurrealDbMemoryClient();
            opts.Namespace = "test";
            opts.Database = "test";
            opts.Schema.For<PocoSnapshot>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            ((List<IProjection>)opts.Projections).Add(projection);
        });
    }
}
