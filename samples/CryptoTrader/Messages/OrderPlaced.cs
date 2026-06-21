namespace CryptoTrader.Messages;

public record OrderPlaced(
    string OrderId,
    string UserId,
    string WalletId,
    string Asset,
    decimal Quantity,
    decimal Price,
    string Side
);
