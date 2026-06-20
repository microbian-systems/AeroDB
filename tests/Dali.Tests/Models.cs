using SurrealDb.Net.Models;

namespace Dali.Tests;

public class Person : Record
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string Email { get; set; } = "";
}

public class Product : Record
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public string Category { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public class Order : Record
{
    public string CustomerName { get; set; } = "";
    public decimal Total { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Tenant-aware model used for tenancy tests.
/// Has a TenantId property that the conjoined tenancy layer automatically populates and filters on.
/// </summary>
public class TenantPerson : Record
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string TenantId { get; set; } = "";
}
