namespace CryptoTrader.Messages;

public record PlaceBuyOrder(
    string UserId,
    string WalletId,
    string Asset,
    decimal Quantity,
    decimal MaxPrice
);
