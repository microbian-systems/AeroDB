// REMOVED: JasperFx.Events, JasperFx.Events.Projections, Marten, Marten.Events.Projections
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
// AeroDB is global using from GlobalUsings.cs

namespace DocSamples;

public class RegisteringProjections
{
    public static async Task register()
    {
        #region sample_registering_projections_with_different_lifecycles

        var builder = Host.CreateApplicationBuilder();
        var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Namespace = "docsamples";
            o.Database = "docsamples";

            // Just in case you need AeroDB to "know" about a projection that will
            // only be calculated "Live", you can register it upfront
            o.Projections.Add<MySpecialProjection>(ProjectionLifecycle.Live);

            // Or instead, we want strong consistency at all times
            // so that the stored projection documents always exactly reflect
            // the
            o.Projections.Add<MySpecialProjection>(ProjectionLifecycle.Inline);

            // Or even differently, we can live with eventual consistency and
            // let Marten use its "Async Daemon" to continuously update the stored
            // documents being built out by our projection in the background
            o.Projections.Add<MySpecialProjection>(ProjectionLifecycle.Async);

            // Just for the sake of completeness, "self-aggregating" types
            // can be registered as projections in AeroDB with this syntax
            // where "Snapshot" now means "a version of the projection from the events"
            o.Projections.Snapshot<QuestParty>(SnapshotLifecycle.Inline);
            o.Projections.Snapshot<QuestParty>(SnapshotLifecycle.Async);

            // This is the equivalent of ProjectionLifecycle.Live
            o.Projections.LiveStreamAggregation<QuestParty>();
        });
        await store.InitializeAsync();
        builder.Services.AddSingleton<IDocumentStore>(store);

        #endregion
    }

    public static async Task register2()
    {
        #region sample_registering_snapshots

        var builder = Host.CreateApplicationBuilder();
        var store = Documents.For(o =>
        {
            o.ClientFactory = () => new SurrealDb.Embedded.InMemory.SurrealDbMemoryClient();
            o.Namespace = "docsamples";
            o.Database = "docsamples";

            // Just for the sake of completeness, "self-aggregating" types
            // can be registered as projections in Marten with this syntax
            // where "Snapshot" now means "a version of the projection from the events"
            o.Projections.Snapshot<QuestParty>(SnapshotLifecycle.Inline);
            o.Projections.Snapshot<QuestParty>(SnapshotLifecycle.Async);

            // This is the equivalent of ProjectionLifecycle.Live
            // This is no longer necessary with Marten 8, but may be necessary
            // for *future* optimizations
            o.Projections.LiveStreamAggregation<QuestParty>();
        });
        await store.InitializeAsync();
        builder.Services.AddSingleton<IDocumentStore>(store);

        #endregion
    }

}

public partial class MySpecialProjection: EventProjection
{
    public override Type[] EventTypes => [];

    public override Task ApplyAsync(IDocumentOperations operations, IEvent e, CancellationToken cancellation)
    {
        // Do whatever this projection does here...
        return base.ApplyAsync(operations, e, cancellation);
    }
}
