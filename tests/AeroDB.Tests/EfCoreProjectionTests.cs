using AeroDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AeroDB.Tests;

// ─── Test event type ────────────────────────────────────────────────

internal sealed class EfCoreOrderCreated
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
}

// ─── Test EF Core projection ────────────────────────────────────────

internal class TestEfCoreProjection : EfCoreEventProjection<DbContext>
{
    public override Type[] EventTypes => [typeof(EfCoreOrderCreated)];

    protected override Task ApplyAsync(DbContext dbContext, IReadOnlyList<IEvent> events, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}

// ─── Tests ──────────────────────────────────────────────────────────

public class EfCoreProjectionTests
{
    [Test]
    public async Task EfCoreProjection_has_event_types()
    {
        var projection = new TestEfCoreProjection();
        projection.EventTypes.ShouldContain(typeof(EfCoreOrderCreated));
    }

    [Test]
    public void EfCoreProjection_default_lifecycle_is_async()
    {
        var projection = new TestEfCoreProjection();
        projection.Lifecycle.ShouldBe(ProjectionLifecycle.Async);
    }

    [Test]
    public void EfCoreProjection_rebuild_returns_completed_task()
    {
        var projection = new TestEfCoreProjection();
        var session = Substitute.For<IDocumentSession>();
        var task = projection.RebuildAsync(session, CancellationToken.None);
        task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Test]
    public void EfCoreProjection_throws_when_no_factory_set()
    {
        var projection = new TestEfCoreProjection();
        var context = Substitute.For<IProjectionContext>();

        var ex = Should.Throw<InvalidOperationException>(async () =>
            await ((IProjection)projection).ApplyAsync(context, CancellationToken.None));

        ex.Message.ShouldContain("DbContext factory");
    }

    [Test]
    public async Task EfCoreProjection_calls_save_changes()
    {
        // Arrange
        var dbContext = Substitute.For<DbContext>();
        var projection = new TestEfCoreProjection();
        projection.SetDbContextFactory(() => dbContext);

        var mockEvent = Substitute.For<IEvent>();
        mockEvent.Data.Returns(new EfCoreOrderCreated { OrderId = "ord-1", Amount = 100m });

        var context = Substitute.For<IProjectionContext>();
        context.TypedEvents.Returns(new List<IEvent> { mockEvent }.AsReadOnly());

        // Act
        await ((IProjection)projection).ApplyAsync(context, CancellationToken.None);

        // Assert
        await dbContext.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public void EfCoreProjection_di_registration_resolves_projection()
    {
        var services = new ServiceCollection();

        // Register a DbContext factory manually (no real DB needed for test)
        services.AddScoped<DbContext>(_ => Substitute.For<DbContext>());

        services.AddEfCoreProjection<DbContext, TestEfCoreProjection>();

        var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<TestEfCoreProjection>();

        resolved.ShouldNotBeNull();
        resolved.EventTypes.ShouldContain(typeof(EfCoreOrderCreated));
    }
}
