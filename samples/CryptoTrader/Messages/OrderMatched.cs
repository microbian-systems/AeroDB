namespace CryptoTrader.Messages;

public record OrderMatched(
    string TradeSagaId,
    string OrderId,
    string UserId,
    string WalletId,
    string Asset,
    decimal Quantity,
    decimal ExecutedPrice,
    decimal Total,
    string Side
);
