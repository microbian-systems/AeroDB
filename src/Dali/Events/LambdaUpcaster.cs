namespace Dali;

internal sealed class LambdaUpcaster<T> : IEventUpcaster where T : class
{
    public string OldEventType { get; }
    private readonly Func<object, T> _upcast;

    public LambdaUpcaster(string oldEventType, Func<object, T> upcast)
    {
        OldEventType = oldEventType;
        _upcast = upcast;
    }

    public object Upcast(object oldEvent) => _upcast(oldEvent);
}
