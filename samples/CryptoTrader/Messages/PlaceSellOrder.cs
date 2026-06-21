namespace CryptoTrader.Messages;

public record PlaceSellOrder(
    string UserId,
    string WalletId,
    string Asset,
    decimal Quantity,
    decimal MinPrice
);
