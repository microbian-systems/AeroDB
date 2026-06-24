namespace CryptoTrader.Services;

/// <summary>
/// Simulates live crypto prices using a random-walk model.
/// Each call to <see cref="GetCurrentPrice"/> updates the underlying price
/// by a random factor between -5% and +5%.
/// </summary>
public sealed class PriceSimulator
{
    private readonly Dictionary<string, decimal> _prices = new()
    {
        ["BTC"] = 68420m,
        ["ETH"] = 3450m,
        ["SOL"] = 145.30m,
        ["MATIC"] = 0.72m
    };

    private readonly Random _rng = new();

    /// <summary>
    /// Returns the current simulated price for the given asset,
    /// applying a random-walk step (-5% to +5%).
    /// </summary>
    public decimal GetCurrentPrice(string asset)
    {
        if (!_prices.ContainsKey(asset))
            return 0;

        var change = (decimal)(_rng.NextDouble() * 0.1 - 0.05); // -5% to +5%
        _prices[asset] *= 1 + change;
        return Math.Round(_prices[asset], 2);
    }

    /// <summary>
    /// Returns the current price without mutating (for reporting).
    /// </summary>
    public decimal GetPrice(string asset)
        => _prices.GetValueOrDefault(asset);
}
