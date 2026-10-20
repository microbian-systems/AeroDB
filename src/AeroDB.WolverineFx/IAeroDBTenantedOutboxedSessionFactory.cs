using AeroDB.Sable;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// Opens a document session bound to the fixed, validated tenant represented by
/// a Wolverine message context. Implementations must not fall back to a shared
/// store when the tenant is missing or unknown.
/// </summary>
public interface IAeroDBTenantedOutboxedSessionFactory
{
    /// <summary>Opens an outbox-enrolled session for the context's fixed tenant.</summary>
    Task<IDocumentSession> OpenSessionAsync(
        MessageContext context,
        CancellationToken cancellationToken = default);
}
