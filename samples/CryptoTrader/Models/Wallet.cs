using AeroDB;
using SurrealDb.Net.Models;

namespace CryptoTrader.Models;

public class Wallet : Record, IVersioned
{
    public string Asset { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public decimal Reserved { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public long Version { get; set; }
}
