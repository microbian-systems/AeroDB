namespace AeroDB;

using System.Reactive.Linq;
using AeroDB.LiveQuery;

public static class ReactiveLinqExtensions
{
    /// <summary>Excludes CLOSE notifications, keeping Open, Created, Updated, Deleted.</summary>
    public static IObservable<AeroDBLiveChange<T>> SelectResults<T>(
        this IObservable<AeroDBLiveChange<T>> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(c => c.Action != AeroDBLiveAction.Closed);
    }

    /// <summary>Projects only CREATE events' documents.</summary>
    public static IObservable<T> SelectOnCreate<T>(
        this IObservable<AeroDBLiveChange<T>> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source
            .Where(c => c.Action == AeroDBLiveAction.Created && c.Document is not null)
            .Select(c => c.Document!);
    }

    /// <summary>Projects only UPDATE events' documents.</summary>
    public static IObservable<T> SelectOnUpdate<T>(
        this IObservable<AeroDBLiveChange<T>> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source
            .Where(c => c.Action == AeroDBLiveAction.Updated && c.Document is not null)
            .Select(c => c.Document!);
    }

    /// <summary>Projects only DELETE events' documents.</summary>
    public static IObservable<T> SelectOnDelete<T>(
        this IObservable<AeroDBLiveChange<T>> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source
            .Where(c => c.Action == AeroDBLiveAction.Deleted && c.Document is not null)
            .Select(c => c.Document!);
    }
}
