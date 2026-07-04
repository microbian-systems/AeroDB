using SurrealDb.Net.Models;

namespace Dali.IntegrationTests;

// Simple person for reactive tests
public class TestPerson : Record
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public string Email { get; set; } = "";
}

// Customer for FETCH integration tests
public class Customer : Record
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

// Order referencing a customer
public class OrderWithCustomer : Record
{
    public string Product { get; set; } = "";
    public Customer? Customer { get; set; }
}

// Order submitted event
public class OrderSubmitted
{
    public string OrderId { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
    public string CustomerName { get; set; } = "";
}

// Product with DateTimeOffset
public class Product : Record
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public string Category { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
