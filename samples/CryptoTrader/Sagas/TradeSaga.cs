using CryptoTrader.Messages;
using AeroDB.Sable;
using SurrealDb.Net.Models;
using Wolverine;

namespace CryptoTrader.Sagas;

/// <summary>
/// Saga document persisted in AeroDB.Sable. Not a Wolverine saga — state is managed
/// manually through AeroDB.Sable document operations in the handlers. This avoids
/// the codegen issues with AeroDB.Sable's outbox session factory for saga chains.
///
/// Demonstrated AeroDB.Sable + Wolverine features:
/// - AeroDB.Sable document persistence for custom saga state
/// - Cascading message flow (PlaceBuyOrder → OrderPlaced → OrderMatched → TradeExecuted)
/// - IAeroDBOp side-effect pattern for wallet updates
/// - Session-based document operations (LoadAsync, Store)
/// </summary>
public class TradeSagaData : Record
{
    public string UserId { get; set; } = string.Empty;
    public string WalletId { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal ExecutedPrice { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = "Pending";
}
