using CryptoTrader.Messages;
using Dali;
using SurrealDb.Net.Models;
using Wolverine;

namespace CryptoTrader.Sagas;

/// <summary>
/// Saga document persisted in Dali. Not a Wolverine saga — state is managed
/// manually through Dali document operations in the handlers. This avoids
/// the codegen issues with Dali's outbox session factory for saga chains.
///
/// Demonstrated Dali + Wolverine features:
/// - Dali document persistence for custom saga state
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
