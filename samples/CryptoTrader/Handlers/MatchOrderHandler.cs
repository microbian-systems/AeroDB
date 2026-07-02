using CryptoTrader.Models;
using CryptoTrader.Messages;
using CryptoTrader.Services;
using Dali;

namespace CryptoTrader.Handlers;

/// <summary>
/// Handles <see cref="OrderPlaced"/> by simulating a market match
/// and recording the trade event.
///
/// Demonstrates:
/// - Dali document CRUD via IDocumentStore (constructor-injected)
/// - Price simulation via external service
/// - Cascading return value (TradeExecuted) for console reporting
/// </summary>
public class MatchOrderHandler
{
    /// <summary>Running counter of settled trades — used by Program.cs for summary.</summary>
    public static int SettledTrades;
    /// <summary>Running total volume — used by Program.cs for summary.</summary>
    public static decimal TotalVolume;
    /// <summary>Per-asset trade counts.</summary>
    public static readonly Dictionary<string, int> AssetCounts = new();
    /// <summary>Per-asset volumes.</summary>
    public static readonly Dictionary<string, decimal> AssetVolumes = new();
    /// <summary>Execution prices for avg calc.</summary>
    public static readonly List<decimal> Prices = new();
    private readonly IDocumentStore _store;
    private readonly PriceSimulator _prices;

    public MatchOrderHandler(IDocumentStore store, PriceSimulator prices)
    {
        _store = store;
        _prices = prices;
    }

    public async Task<TradeExecuted> Handle(OrderPlaced placed)
    {
        await using var session = await _store.OpenSessionAsync(new SessionOptions { Tracking = DocumentTracking.None });

        // 1. Get current market price
        var marketPrice = _prices.GetCurrentPrice(placed.Asset);

        // 2. Determine execution price
        var execPrice = placed.Side == "buy"
            ? Math.Min(marketPrice, placed.Price)
            : Math.Max(marketPrice, placed.Price);

        var total = execPrice * placed.Quantity;

        // 3. Record trade event
        var tradeEvent = new TradeEvent
        {
            Type = "trade",
            Asset = placed.Asset,
            Quantity = placed.Quantity,
            Price = execPrice,
            Total = total,
            WalletId = placed.WalletId,
            SagaId = placed.OrderId,
            Status = "settled",
            Timestamp = DateTime.UtcNow
        };
        session.Store(tradeEvent);

        await session.SaveChangesAsync();

        // 4. Update running counters for summary reporting
        Interlocked.Increment(ref SettledTrades);
        lock (AssetCounts) { AssetCounts[placed.Asset] = AssetCounts.GetValueOrDefault(placed.Asset) + 1; }
        lock (AssetVolumes) { AssetVolumes[placed.Asset] = AssetVolumes.GetValueOrDefault(placed.Asset) + total; }
        lock (Prices) { TotalVolume += total; Prices.Add(execPrice); }

        // 5. Console output
        var color = placed.Side == "buy" ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.ForegroundColor = color;
        Console.WriteLine(
            $"  [TRADE] {placed.Side.ToUpperInvariant(),4} {placed.Quantity,8:F4} {placed.Asset,-5} " +
            $"@ ${execPrice,8:F2} = ${total,10:F2}"
        );
        Console.ResetColor();

        // 5. Return cascading event
        return new TradeExecuted(
            placed.OrderId,
            placed.UserId,
            placed.WalletId,
            placed.Asset,
            placed.Quantity,
            execPrice,
            total,
            placed.Side,
            0
        );
    }
}
