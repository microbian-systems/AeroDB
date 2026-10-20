using AeroDB.Sable;

namespace AeroDB.PackageConsumer.Tests;

/// <summary>
/// A representative consumer-owned document. Its generated materializer must
/// be supplied by the analyzer assets carried by the AeroDB.Sable package.
/// </summary>
public sealed class PackageConsumerDocument : SableDocument<long>
{
    public string SolicitationNumber { get; set; } = string.Empty;

    public int RequiredTechnicians { get; set; }
}
