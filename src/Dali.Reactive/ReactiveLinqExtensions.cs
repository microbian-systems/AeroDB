namespace Dali;

using System.Reactive.Linq;
using Dali.LiveQuery;

public static class ReactiveLinqExtensions
{
    /// <summary>Excludes CLOSE notifications, keeping Open, Created, Updated, Deleted.</summary>
    public static IObservable<DaliLiveChange<T>> SelectResults<T>(
        this IObservable<DaliLiveChange<T>> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(c => c.Action != DaliLiveAction.Closed);
    }

    /// <summary>Projects only CREATE events' documents.</summary>
    public static IObservable<T> SelectCreatedRecords<T>(
        this IObservable<DaliLiveChange<T>> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source
            .Where(c => c.Action == DaliLiveAction.Created && c.Document is not null)
            .Select(c => c.Document!);
    }

    /// <summary>Projects only UPDATE events' documents.</summary>
    public static IObservable<T> SelectUpdatedRecords<T>(
        this IObservable<DaliLiveChange<T>> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source
            .Where(c => c.Action == DaliLiveAction.Updated && c.Document is not null)
            .Select(c => c.Document!);
    }

    /// <summary>Projects only DELETE events' documents.</summary>
    public static IObservable<T> SelectDeletedRecords<T>(
        this IObservable<DaliLiveChange<T>> source) where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source
            .Where(c => c.Action == DaliLiveAction.Deleted && c.Document is not null)
            .Select(c => c.Document!);
    }
}
