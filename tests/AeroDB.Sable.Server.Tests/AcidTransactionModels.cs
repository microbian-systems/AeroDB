using SurrealDb.Net.Models;

namespace AeroDB.Sable.Server.Tests;

public sealed class Customer
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class Product
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
}

public sealed class Order
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public long ProductId { get; set; }
    public decimal Total { get; set; }
}

public sealed class ExternalProduct
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class SessionDocument
{
    public Guid Id { get; set; }
    public string Subject { get; set; } = string.Empty;
}

public sealed class Purchased : EdgeRecord
{
    public DateTimeOffset PurchasedAt { get; set; }
}

internal sealed class TestClock(DateTimeOffset utcNow)
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}
