using AeroDB;

namespace CryptoTrader.Models;

/// <summary>
/// Edge record for User → holds_asset → CryptoAsset relationship.
/// Inherits from EdgeRecord which extends RelationRecord with In/Out properties.
/// </summary>
public class HoldsAsset : EdgeRecord
{
    public decimal AveragePrice { get; set; }
    public decimal TotalQuantity { get; set; }
    public DateTime FirstAcquired { get; set; }
}
