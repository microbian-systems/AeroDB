using SurrealDb.Net.Models;

namespace CryptoTrader.Models;

public class User : Record
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
