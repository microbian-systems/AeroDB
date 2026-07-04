using CryptoTrader.Messages;
using AeroDB;
using SurrealDb.Net.Models;
using Wolverine;

namespace CryptoTrader.Sagas;

/// <summary>
/// Saga document persisted in AeroDB. Not a Wolverine saga — state is managed
/// manually through AeroDB document operations in the handlers. This avoids
/// the codegen issues with AeroDB's outbox session factory for saga chains.
///
/// Demonstrated AeroDB + Wolverine features:
/// - AeroDB document persistence for custom saga state
/// - Cascading message flow (PlaceBuyOrder → OrderPlaced → OrderMatched → TradeExecuted)
/// - IDaliOp side-effect pattern for wallet updates
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
