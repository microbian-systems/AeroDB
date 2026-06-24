using SurrealDb.Net.Models;

namespace CryptoTrader.Models;

public class Account : Record
{
    public string Name { get; set; } = string.Empty;
    public decimal UsdBalance { get; set; } = 100_000m;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
