using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

using System.Linq.Expressions;

/// <summary>
/// Fluent configuration for a typed domain event stream.
/// Defines the event table, tracked event types, stream identity, versioning, and serialization.
/// Implements <see cref="IEventStreamConfiguration"/> for integration with <see cref="IAeroSchemaBuilder"/>.
/// </summary>
public class EventStreamConfiguration : IEventStreamConfiguration
{
    /// <summary>The CLR event type stored in the stream table (row type).</summary>
    public Type RowType { get; private set; } = default!;

    /// <summary>The SurrealDB table name for the event stream.</summary>
    public string TableName { get; private set; } = default!;

    /// <summary>Event types that can be appended to this stream (for compile-time safety).</summary>
    public IReadOnlyList<Type> EventTypes => _eventTypes;
    private readonly List<Type> _eventTypes = new();

    /// <summary>Expression to extract the stream ID from an event row.</summary>
    internal LambdaExpression? StreamIdExpression { get; private set; }

    /// <summary>Expression to extract the version from an event row.</summary>
    internal LambdaExpression? VersionExpression { get; private set; }

    /// <summary>Serialization mode for event payloads. Default: Json.</summary>
    public EventSerializationMode SerializationMode { get; private set; } = EventSerializationMode.Json;

    /// <summary>
    /// Sets the row type (event table record type) for this stream.
    /// </summary>
    /// <typeparam name="TEventRow">The CLR type representing the event table row.</typeparam>
    /// <returns>This configuration instance for chaining.</returns>
    public EventStreamConfiguration UseTable<TEventRow>() where TEventRow : class
    {
        RowType = typeof(TEventRow);
        TableName = MetadataDispatch.GetTableName(typeof(TEventRow));
        return this;
    }

    /// <summary>
    /// Registers a single event type that can be appended to this stream.
    /// Provides compile-time type safety for append operations.
    /// </summary>
    public EventStreamConfiguration Events<T1>()
    {
        _eventTypes.Add(typeof(T1));
        return this;
    }

    /// <summary>
    /// Registers two event types that can be appended to this stream.
    /// </summary>
    public EventStreamConfiguration Events<T1, T2>()
    {
        _eventTypes.Add(typeof(T1));
        _eventTypes.Add(typeof(T2));
        return this;
    }

    /// <summary>
    /// Registers three event types that can be appended to this stream.
    /// </summary>
    public EventStreamConfiguration Events<T1, T2, T3>()
    {
        _eventTypes.Add(typeof(T1));
        _eventTypes.Add(typeof(T2));
        _eventTypes.Add(typeof(T3));
        return this;
    }

    /// <summary>
    /// Registers four event types that can be appended to this stream.
    /// </summary>
    public EventStreamConfiguration Events<T1, T2, T3, T4>()
    {
        _eventTypes.Add(typeof(T1));
        _eventTypes.Add(typeof(T2));
        _eventTypes.Add(typeof(T3));
        _eventTypes.Add(typeof(T4));
        return this;
    }

    /// <summary>
    /// Registers event types using a runtime params array.
    /// </summary>
    public EventStreamConfiguration Events(params Type[] eventTypes)
    {
        foreach (var t in eventTypes)
            _eventTypes.Add(t);
        return this;
    }

    /// <summary>
    /// Sets the expression to extract the stream ID from an event row.
    /// Example: <c>x => x.StreamId</c>
    /// </summary>
    /// <typeparam name="TEventRow">The event row type.</typeparam>
    /// <typeparam name="TProp">The property type of the stream ID.</typeparam>
    /// <param name="expression">Expression selecting the stream ID property.</param>
    /// <returns>This configuration instance for chaining.</returns>
    public EventStreamConfiguration StreamId<TEventRow, TProp>(Expression<Func<TEventRow, TProp>> expression)
    {
        StreamIdExpression = expression;
        return this;
    }

    /// <summary>
    /// Sets the expression to extract the version from an event row.
    /// Example: <c>x => x.Version</c>
    /// </summary>
    /// <typeparam name="TEventRow">The event row type.</typeparam>
    /// <typeparam name="TProp">The property type of the version.</typeparam>
    /// <param name="expression">Expression selecting the version property.</param>
    /// <returns>This configuration instance for chaining.</returns>
    public EventStreamConfiguration Version<TEventRow, TProp>(Expression<Func<TEventRow, TProp>> expression)
    {
        VersionExpression = expression;
        return this;
    }

    /// <summary>
    /// Sets the serialization mode for event payloads.
    /// </summary>
    /// <param name="mode">The serialization mode (Json or Binary).</param>
    /// <returns>This configuration instance for chaining.</returns>
    public EventStreamConfiguration Serialization(EventSerializationMode mode)
    {
        SerializationMode = mode;
        return this;
    }
}
