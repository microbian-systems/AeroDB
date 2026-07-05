namespace AeroDB;

using System.Reactive.Linq;
using AeroDB.LiveQuery;

public static class StateAccumulatorExtensions
{
    /// <summary>
    /// Accumulates CREATE, UPDATE, and DELETE events into a dictionary.
    /// Emits the FINAL accumulated state once the source completes.
    /// </summary>
    public static IObservable<IDictionary<string, T>> AggregateRecords<T>(
        this IObservable<AeroDBLiveChange<T>> source,
        IDictionary<string, T> seed) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(seed);

        return source.Aggregate(seed, AccumulateRecord);
    }

    /// <summary>
    /// Accumulates CREATE, UPDATE, and DELETE events into a dictionary.
    /// Emits the accumulated state after EVERY event (incremental).
    /// </summary>
    public static IObservable<IDictionary<string, T>> ScanRecords<T>(
        this IObservable<AeroDBLiveChange<T>> source,
        IDictionary<string, T> seed) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(seed);

        return source.Scan(seed, AccumulateRecord);
    }

    private static IDictionary<string, T> AccumulateRecord<T>(
        IDictionary<string, T> acc,
        AeroDBLiveChange<T> change) where T : class
    {
        switch (change.Action)
        {
            case AeroDBLiveAction.Created when change.Id is not null && change.Document is not null:
                acc[change.Id] = change.Document;
                break;
            case AeroDBLiveAction.Updated when change.Id is not null && change.Document is not null:
                acc[change.Id] = change.Document;
                break;
            case AeroDBLiveAction.Deleted when change.Id is not null:
                acc.Remove(change.Id);
                break;
        }
        return acc;
    }
}
