using System.Threading.Channels;

namespace AeroDB.Sable.LiveQuery;

public interface IAeroDBLiveQuery<T> : IAsyncDisposable
{
    IAsyncEnumerable<AeroDBLiveChange<T>> Changes(CancellationToken ct = default);

    /// <summary>All create, update, and delete results — excludes Open and Close events.</summary>
    IAsyncEnumerable<AeroDBLiveChange<T>> GetResults(CancellationToken ct = default);

    /// <summary>Only created records (documents only, not the change wrapper).</summary>
    IAsyncEnumerable<T> GetCreatedRecords(CancellationToken ct = default);

    /// <summary>Only updated records (documents only, not the change wrapper).</summary>
    IAsyncEnumerable<T> GetUpdatedRecords(CancellationToken ct = default);

    /// <summary>Only deleted records (documents only, not the change wrapper).</summary>
    IAsyncEnumerable<T> GetDeletedRecords(CancellationToken ct = default);

    ChannelReader<AeroDBLiveChange<T>>? Reader { get; }

    Task StopAsync(CancellationToken ct = default);
}
