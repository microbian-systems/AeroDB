using AeroDB;
using SurrealDb.Embedded.InMemory;

var store = Documents.For(o =>
{
    o.ClientFactory = () => new SurrealDbMemoryClient();
    o.Namespace = "warehouse";
    o.Database = "warehouse";
    o.Events.Enabled = true;
    o.Schema.For<WarehouseProductReadModel>().Identity(x => x.Id).SetSchemaMode(SchemaMode.Flexible);
    o.Projections.Add(new WarehouseProductProjection());
});
await store.InitializeAsync();

var id = Guid.NewGuid();

var warehouseRepository = new WarehouseRepository(store);

var warehouseProductReadModel = await warehouseRepository.GetAsync(id);

DemoConsole.WriteWithColour($"{warehouseProductReadModel?.QuantityOnHand ?? 0} items of stock in the warehouse for {id}");

var handler = new WarehouseProductHandler(id, store);
await handler.ReceiveProduct(100);

DemoConsole.WriteWithColour($"Received 100 items of stock into the warehouse for {id}");

await handler.ShipProduct(10);

DemoConsole.WriteWithColour($"Shipped 10 items of stock out of the warehouse for {id}");

await handler.AdjustInventory(5, "Ordered too many");

DemoConsole.WriteWithColour($"Found 5 items of stock hiding in the warehouse for {id} and have adjusted the stock count");

warehouseProductReadModel = await warehouseRepository.GetAsync(id);

DemoConsole.WriteWithColour($"{warehouseProductReadModel!.QuantityOnHand} items of stock in the warehouse for {warehouseProductReadModel.Id}");


public record ProductShipped(Guid Id, int Quantity, DateTime DateTime);

public record ProductReceived(Guid Id, int Quantity, DateTime DateTime);

public record InventoryAdjusted(Guid Id, int Quantity, string Reason, DateTime DateTime);


public class WarehouseRepository
{
    private readonly IDocumentStore store;

    public WarehouseRepository(IDocumentStore store)
    {
        this.store = store;
    }

    public async Task<WarehouseProductReadModel?> GetAsync(Guid id)
    {
        await using var session = await store.QuerySessionAsync();

        var doc = await session.LoadAsync<WarehouseProductReadModel>(id);
        return doc;
    }
}

public class WarehouseProductReadModel
{
    public Guid Id { get; set; }
    public int QuantityOnHand { get; set; }
}

public partial class WarehouseProductProjection : SingleStreamProjection<WarehouseProductReadModel>
{
    public override ProjectionLifecycle Lifecycle => ProjectionLifecycle.Inline;

    public override Type[] EventTypes => new[]
    {
        typeof(ProductShipped), typeof(ProductReceived), typeof(InventoryAdjusted)
    };

    public void Apply(WarehouseProductReadModel readModel, ProductShipped evnt)
    {
        readModel.QuantityOnHand -= evnt.Quantity;
    }

    public void Apply(WarehouseProductReadModel readModel, ProductReceived evnt)
    {
        readModel.QuantityOnHand += evnt.Quantity;
    }

    public void Apply(WarehouseProductReadModel readModel, InventoryAdjusted evnt)
    {
        readModel.QuantityOnHand += evnt.Quantity;
    }

    protected override object GetDocumentId(IReadOnlyList<object> events)
    {
        foreach (var e in events)
        {
            if (e is ProductShipped ps) return ps.Id;
            if (e is ProductReceived pr) return pr.Id;
            if (e is InventoryAdjusted ia) return ia.Id;
        }
        return Guid.NewGuid();
    }

    protected override WarehouseProductReadModel? ApplyEvents(
        WarehouseProductReadModel? aggregate,
        IReadOnlyList<object> events,
        CancellationToken ct)
    {
        var readModel = aggregate ?? new WarehouseProductReadModel();
        foreach (var e in events)
        {
            switch (e)
            {
                case ProductShipped ps:
                    Apply(readModel, ps);
                    break;
                case ProductReceived pr:
                    Apply(readModel, pr);
                    break;
                case InventoryAdjusted ia:
                    Apply(readModel, ia);
                    break;
            }
        }
        return readModel;
    }
}

public class WarehouseProductWriteModel
{
    public Guid Id { get; set; }
    public int QuantityOnHand { get; set; }

    public void Apply(ProductShipped evnt)
    {
        Id = evnt.Id;
        QuantityOnHand -= evnt.Quantity;
    }

    public void Apply(ProductReceived evnt)
    {
        Id = evnt.Id;
        QuantityOnHand += evnt.Quantity;
    }

    public void Apply(InventoryAdjusted evnt)
    {
        Id = evnt.Id;
        QuantityOnHand += evnt.Quantity;
    }
}

public class WarehouseProductHandler
{
    private readonly Guid id;
    private readonly IDocumentStore store;

    public WarehouseProductHandler(Guid id, IDocumentStore store)
    {
        this.id = id;
        this.store = store;
    }

    public async Task ShipProduct(int quantity)
    {
        await using var session = await store.LightweightSessionAsync();

        var warehouseProduct = await session.Events.AggregateStreamAsync<WarehouseProductWriteModel>(id.ToString());

        if (quantity > warehouseProduct?.QuantityOnHand)
        {
            throw new InvalidDomainException("Ah... we don't have enough product to ship?");
        }

        await session.Events.Append(id.ToString(), [new ProductShipped(id, quantity, DateTime.UtcNow)]);
        await session.SaveChangesAsync();
    }

    public async Task ReceiveProduct(int quantity)
    {
        await using var session = await store.LightweightSessionAsync();

        await session.Events.Append(id.ToString(), [new ProductReceived(id, quantity, DateTime.UtcNow)]);
        await session.SaveChangesAsync();
    }

    public async Task AdjustInventory(int quantity, string reason)
    {
        await using var session = await store.LightweightSessionAsync();

        var warehouseProduct = await session.Events.AggregateStreamAsync<WarehouseProductWriteModel>(id.ToString());

        if (warehouseProduct?.QuantityOnHand + quantity < 0)
        {
            throw new InvalidDomainException("Cannot adjust to a negative quantity on hand.");
        }

        await session.Events.Append(id.ToString(), [new InventoryAdjusted(id, quantity, reason, DateTime.UtcNow)]);
        await session.SaveChangesAsync();
    }
}

public class InvalidDomainException : Exception
{
    public InvalidDomainException(string message) : base(message)
    {
    }
}

public static class DemoConsole
{
    public static void WriteWithColour(string value)
    {
        lock (Console.Out)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(value);
            Console.ForegroundColor = ConsoleColor.White;
        }
    }
}
