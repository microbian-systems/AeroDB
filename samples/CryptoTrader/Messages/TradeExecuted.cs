namespace CryptoTrader.Messages;

public record TradeExecuted(
    string OrderId,
    string UserId,
    string WalletId,
    string Asset,
    decimal Quantity,
    decimal Price,
    decimal Total,
    string Side,
    decimal NewBalance
);
