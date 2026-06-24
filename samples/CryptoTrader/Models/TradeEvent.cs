using SurrealDb.Net.Models;

namespace CryptoTrader.Models;

public class TradeEvent : Record
{
    public string Type { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Total { get; set; }
    public string WalletId { get; set; } = string.Empty;
    public string SagaId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
