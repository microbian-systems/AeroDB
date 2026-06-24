using CryptoTrader.Messages;
using CryptoTrader.Sagas;
using Dali;
using SurrealDb.Net.Models;

namespace CryptoTrader.Handlers;

/// <summary>
/// Handles initial buy/sell order placement by creating a saga record in Dali.
/// Uses explicit session management (IDocumentStore via constructor) to avoid
/// codegen issues with Dali's auto-transaction support.
///
/// Demonstrates:
/// - Dali document CRUD (manual session open/save)
/// - IDocumentStore dependency injection
/// - Cascading message return (OrderPlaced)
/// </summary>
public class PlaceOrderHandler
{
    private readonly IDocumentStore _store;

    public PlaceOrderHandler(IDocumentStore store)
    {
        _store = store;
    }

    public async Task<OrderPlaced> Handle(PlaceBuyOrder cmd)
    {
        var sagaId = Guid.NewGuid().ToString("N");

        await using var session = await _store.LightweightSessionAsync();
        var sagaData = new TradeSagaData
        {
            Id = new RecordIdOf<string>("trade_saga_data", sagaId),
            UserId = cmd.UserId,
            WalletId = cmd.WalletId,
            Asset = cmd.Asset,
            Side = "buy",
            Quantity = cmd.Quantity,
            Price = cmd.MaxPrice,
            Status = "Pending"
        };
        session.Store(sagaData);
        await session.SaveChangesAsync();

        return new OrderPlaced(
            sagaId, cmd.UserId, cmd.WalletId, cmd.Asset,
            cmd.Quantity, cmd.MaxPrice, "buy");
    }

    public async Task<OrderPlaced> Handle(PlaceSellOrder cmd)
    {
        var sagaId = Guid.NewGuid().ToString("N");

        await using var session = await _store.LightweightSessionAsync();
        var sagaData = new TradeSagaData
        {
            Id = new RecordIdOf<string>("trade_saga_data", sagaId),
            UserId = cmd.UserId,
            WalletId = cmd.WalletId,
            Asset = cmd.Asset,
            Side = "sell",
            Quantity = cmd.Quantity,
            Price = cmd.MinPrice,
            Status = "Pending"
        };
        session.Store(sagaData);
        await session.SaveChangesAsync();

        return new OrderPlaced(
            sagaId, cmd.UserId, cmd.WalletId, cmd.Asset,
            cmd.Quantity, cmd.MinPrice, "sell");
    }
}
