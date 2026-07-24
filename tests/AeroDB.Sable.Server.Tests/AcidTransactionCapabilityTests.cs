using Bogus;
using SurrealDb.Net.Models;

namespace AeroDB.Sable.Server.Tests;

[Category("ServerIntegration")]
[NotInParallel]
public sealed class AcidTransactionCapabilityTests
{
    [Test]
    public async Task Sqc02_OrderEdgeAndInventoryUpdateCommitAtomically(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            "sqc_02_acid_transactions",
            options =>
            {
                options.Schema.For<Customer>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
                options.Schema.For<Product>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
                options.Schema.For<Order>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            },
            ct);

        await using var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None },
            ct);

        await ResetScenarioAsync(session, ct);

        var data = CreateScenarioData();
        var customer = data.Customer;
        var product = data.Product;
        var order = data.Order;
        var clock = new TestClock(data.PurchasedAt);

        session.Store(customer);
        session.Store(product);
        await session.SaveChangesAsync(ct);

        var customerRecordId = RecordIdFor(customer);
        var productRecordId = RecordIdFor(product);

        // This is the SQC-02 public example from sable-query-contracts.md.
        await using var transaction = await session.BeginTransactionAsync(ct);

        session.Store(order);
        session.Relate<Purchased>(customerRecordId, productRecordId, new Purchased
        {
            PurchasedAt = clock.UtcNow
        });
        session.Patch<Product>(product.Id).Increment(x => x.Stock, -1);

        await session.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await using var verification = await store.QuerySessionAsync(ct);
        var savedOrder = await verification.LoadAsync<Order>(order.Id, ct);
        var savedProduct = await verification.LoadAsync<Product>(product.Id, ct);
        var purchasedEdges = await verification.Query<Purchased>().ToListAsync(ct);
        var edgeIdentityProbes = await verification.RawQueryAsync<EdgeIdentityProbe>(
            """
            SELECT
                record::id(in) AS in_id,
                type::is_int(record::id(in)) AS in_is_numeric,
                record::id(out) AS out_id,
                type::is_int(record::id(out)) AS out_is_numeric
            FROM purchased;
            """,
            null,
            ct);

        savedOrder.ShouldNotBeNull();
        savedOrder.CustomerId.ShouldBe(customer.Id);
        savedOrder.ProductId.ShouldBe(product.Id);
        savedOrder.Total.ShouldBe(product.Price);

        savedProduct.ShouldNotBeNull();
        savedProduct.Stock.ShouldBe(data.InitialStock - 1);

        purchasedEdges.Count.ShouldBe(1);
        purchasedEdges[0].PurchasedAt.ShouldBe(clock.UtcNow);
        edgeIdentityProbes.Count.ShouldBe(1);
        edgeIdentityProbes[0].InId.ShouldBe(customer.Id);
        edgeIdentityProbes[0].InIsNumeric.ShouldBeTrue();
        edgeIdentityProbes[0].OutId.ShouldBe(product.Id);
        edgeIdentityProbes[0].OutIsNumeric.ShouldBeTrue();
    }

    [Test]
    public async Task Sqc02_RollbackLeavesNoPartialMultiModelState(CancellationToken ct)
    {
        await using var store = await ServerTestHarness.CreateStoreAsync(
            "sqc_02_acid_transactions",
            options =>
            {
                options.Schema.For<Customer>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
                options.Schema.For<Product>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
                options.Schema.For<Order>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
            },
            ct);

        await using var session = await store.OpenSessionAsync(
            new SessionOptions { Tracking = DocumentTracking.None },
            ct);

        await ResetScenarioAsync(session, ct);

        var data = CreateScenarioData();
        session.Store(data.Customer);
        session.Store(data.Product);
        await session.SaveChangesAsync(ct);

        var customerRecordId = RecordIdFor(data.Customer);
        var productRecordId = RecordIdFor(data.Product);

        await using (var transaction = await session.BeginTransactionAsync(ct))
        {
            session.Store(data.Order);
            session.Relate<Purchased>(customerRecordId, productRecordId, new Purchased
            {
                PurchasedAt = data.PurchasedAt
            });
            session.Patch<Product>(data.Product.Id).Increment(x => x.Stock, -1);

            await session.SaveChangesAsync(ct);
            await transaction.RollbackAsync(ct);
        }

        await using var verification = await store.QuerySessionAsync(ct);
        var savedOrder = await verification.LoadAsync<Order>(data.Order.Id, ct);
        var savedProduct = await verification.LoadAsync<Product>(data.Product.Id, ct);
        var purchasedEdges = await verification.Query<Purchased>().ToListAsync(ct);

        savedOrder.ShouldBeNull();
        savedProduct.ShouldNotBeNull();
        savedProduct.Stock.ShouldBe(data.InitialStock);
        purchasedEdges.ShouldBeEmpty();
    }

    private static async Task ResetScenarioAsync(IDocumentSession session, CancellationToken ct)
    {
        await session.ExecuteSqlAsync(
            "DELETE purchased; DELETE `order`; DELETE product; DELETE customer;",
            null,
            ct);
    }

    private static RecordId RecordIdFor<T>(T entity)
        where T : class
    {
        var id = entity switch
        {
            Customer customer => customer.Id,
            Product product => product.Id,
            _ => throw new ArgumentException($"Unsupported record type '{typeof(T).Name}'.", nameof(entity))
        };

        var table = entity switch
        {
            Customer => "customer",
            Product => "product",
            _ => throw new ArgumentException($"Unsupported record type '{typeof(T).Name}'.", nameof(entity))
        };

        return new RecordIdOf<long>(table, id);
    }

    private static AcidScenarioData CreateScenarioData()
    {
        const int initialStock = 7;
        var customer = new Faker<Customer>("en")
            .UseSeed(1302)
            .RuleFor(x => x.Id, faker => faker.Random.Long(10_000, 99_999))
            .RuleFor(x => x.Name, faker => faker.Name.FullName())
            .RuleFor(x => x.Email, faker => faker.Internet.Email())
            .Generate();
        var product = new Faker<Product>("en")
            .UseSeed(1303)
            .RuleFor(x => x.Id, faker => faker.Random.Long(100_000, 199_999))
            .RuleFor(x => x.Name, faker => faker.Commerce.ProductName())
            .RuleFor(x => x.Price, faker => faker.Finance.Amount(25, 500, 2))
            .RuleFor(x => x.Stock, initialStock)
            .Generate();
        var order = new Faker<Order>("en")
            .UseSeed(1304)
            .RuleFor(x => x.Id, faker => faker.Random.Long(200_000, 299_999))
            .RuleFor(x => x.CustomerId, customer.Id)
            .RuleFor(x => x.ProductId, product.Id)
            .RuleFor(x => x.Total, product.Price)
            .Generate();

        return new AcidScenarioData(
            customer,
            product,
            order,
            initialStock,
            new DateTimeOffset(2026, 7, 22, 12, 30, 0, TimeSpan.Zero));
    }

    private sealed record AcidScenarioData(
        Customer Customer,
        Product Product,
        Order Order,
        int InitialStock,
        DateTimeOffset PurchasedAt);

    private sealed class EdgeIdentityProbe
    {
        public long InId { get; set; }
        public bool InIsNumeric { get; set; }
        public long OutId { get; set; }
        public bool OutIsNumeric { get; set; }
    }
}
