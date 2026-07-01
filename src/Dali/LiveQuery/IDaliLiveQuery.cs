using System.Threading.Channels;

namespace Dali.LiveQuery;

public interface IDaliLiveQuery<T> : IAsyncDisposable
{
    IAsyncEnumerable<DaliLiveChange<T>> Changes(CancellationToken ct = default);

    ChannelReader<DaliLiveChange<T>>? Reader { get; }

    Task StopAsync(CancellationToken ct = default);
}
