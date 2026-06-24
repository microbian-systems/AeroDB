using SurrealDb.Net.Models;

namespace CryptoTrader.Models;

/// <summary>
/// Graph node representing a crypto currency.
/// Id format: crypto_asset:{Ticker} (e.g., crypto_asset:BTC)
/// </summary>
public class CryptoAsset : Record
{
    public string Ticker { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal CurrentPrice { get; set; }
}
