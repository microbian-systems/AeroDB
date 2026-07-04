using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using AeroDB.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB;

/// <summary>
/// Declarative event → table column mapping projection.
/// Maps event properties to table columns via LINQ expressions.
/// </summary>
/// <typeparam name="TDoc">The projected document/row type (must extend <see cref="Record"/> and have a parameterless constructor).</typeparam>
/// <typeparam name="TId">The type of the identifier used to derive the document ID from events.</typeparam>
public class FlatTableProjection<TDoc, TId> : IProjection
    where TDoc : Record, new()
{
    private readonly string _tableName;
    private readonly Func<IReadOnlyList<object>, TId> _getDocumentId;
    private readonly List<ColumnMapping> _mappings = new();
    private ProjectionLifecycle _lifecycle = ProjectionLifecycle.Inline;

    /// <summary>
    /// Creates a flat table projection that maps events to columns in <paramref name="tableName"/>.
    /// </summary>
    /// <param name="tableName">The SurrealDB table to store projected rows in.</param>
    /// <param name="getDocumentId">A function that extracts the document identifier from the event batch.</param>
    public FlatTableProjection(string tableName, Func<IReadOnlyList<object>, TId> getDocumentId)
    {
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        _getDocumentId = getDocumentId ?? throw new ArgumentNullException(nameof(getDocumentId));
    }

    /// <inheritdoc />
    public string Name => GetType().Name;

    /// <inheritdoc />
    public Type[] EventTypes => _mappings.Select(m => m.EventType).Distinct().ToArray();

    /// <inheritdoc />
    public ProjectionLifecycle Lifecycle => _lifecycle;

    /// <summary>
    /// Set the projection lifecycle. Default is <see cref="ProjectionLifecycle.Inline"/>.
    /// </summary>
    public FlatTableProjection<TDoc, TId> Life(ProjectionLifecycle lifecycle)
    {
        _lifecycle = lifecycle;
        return this;
    }

    /// <summary>
    /// Map an event property to a document column. When the mapped event is processed,
    /// the event property value is extracted and written to the corresponding document property.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="eventProperty">Expression selecting the event property (e.g., <c>e => e.Name</c>).</param>
    /// <param name="documentProperty">Expression selecting the document property (e.g., <c>doc => doc.Name</c>).</param>
    public FlatTableProjection<TDoc, TId> Project<TEvent>(
        Expression<Func<TEvent, object>> eventProperty,
        Expression<Func<TDoc, object>> documentProperty)
    {
        ArgumentNullException.ThrowIfNull(eventProperty);
        ArgumentNullException.ThrowIfNull(documentProperty);

        _mappings.Add(new ColumnMapping(typeof(TEvent), eventProperty, documentProperty));
        return this;
    }

    /// <summary>
    /// Provide a custom value for a column based on the event, without a direct event property mapping.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="documentProperty">Expression selecting the document property to set.</param>
    /// <param name="valueProvider">A function that produces the column value from the event.</param>
    public FlatTableProjection<TDoc, TId> Set<TEvent>(
        Expression<Func<TDoc, object>> documentProperty,
        Func<TEvent, object> valueProvider)
    {
        ArgumentNullException.ThrowIfNull(documentProperty);
        ArgumentNullException.ThrowIfNull(valueProvider);

        _mappings.Add(new ColumnMapping(typeof(TEvent), null, documentProperty)
        {
            CustomValueProvider = e => valueProvider((TEvent)e)
        });
        return this;
    }

    /// <summary>
    /// Delete documents when an event matching the given predicate is processed.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="predicate">A predicate that returns <c>true</c> when the document should be deleted.</param>
    public FlatTableProjection<TDoc, TId> Delete<TEvent>(
        Func<TEvent, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        _mappings.Add(new ColumnMapping(typeof(TEvent), null, null)
        {
            DeletePredicate = e => predicate((TEvent)e)
        });
        return this;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(IProjectionContext context, CancellationToken ct)
    {
        var events = context.TypedEvents ?? [];
        if (events.Count == 0) return;

        var docId = _getDocumentId(events.Select(e => e.Data).ToList().AsReadOnly());
        var docIdString = docId?.ToString();

        // Load existing document
        TDoc? doc = null;
        if (!string.IsNullOrEmpty(docIdString))
        {
            try
            {
                doc = await context.Session.LoadAsync<TDoc>(docIdString, ct).ConfigureAwait(false);
            }
            catch
            {
                // New document — will be created below
            }
        }
        doc ??= new TDoc();

        bool shouldDelete = false;
        bool hasChanges = false;

        foreach (var e in events)
        {
            var eventData = e.Data;
            if (eventData is null) continue;

            var eventType = eventData.GetType();

            // Find matching column mappings for this event type
            foreach (var mapping in _mappings.Where(m => m.EventType == eventType))
            {
                if (mapping.DeletePredicate is not null)
                {
                    if (mapping.DeletePredicate(eventData))
                        shouldDelete = true;
                    continue;
                }

                if (mapping.CustomValueProvider is not null)
                {
                    var value = mapping.CustomValueProvider(eventData);
                    SetPropertyValue(doc, mapping.DocumentProperty!, value);
                    hasChanges = true;
                }
                else if (mapping.EventProperty is not null)
                {
                    var value = GetEventPropertyValue(eventData, mapping.EventProperty);
                    SetPropertyValue(doc, mapping.DocumentProperty!, value);
                    hasChanges = true;
                }
            }
        }

        if (shouldDelete)
        {
            context.Session.Delete(doc);
        }
        else if (hasChanges)
        {
            if (!string.IsNullOrEmpty(docIdString))
            {
                // Use the type-derived table name so DocumentSession's Phase 5 persistence
                // constructs a matching RecordId during Upsert. The _tableName is used
                // for document identity tracking within this projection.
                var storageTable = MetadataDispatch.GetTableName(typeof(TDoc));
                doc.Id = new RecordIdOf<string>(storageTable, docIdString);
            }
            context.Session.Store(doc);
        }
    }

    /// <inheritdoc />
    public async Task RebuildAsync(IDocumentSession session, CancellationToken ct)
    {
        var eventTypeNames = EventTypes.Select(t => t.Name).ToList();
        if (eventTypeNames.Count == 0) return;

        var typeFilter = string.Join(", ", eventTypeNames.Select(n => $"'{n}'"));
        var sql = $"SELECT * FROM mt_events WHERE event_type IN [{typeFilter}] ORDER BY stream_id ASC, version ASC";

        if (session is not InternalSessionBase internalSession)
            return;

        var surrealSession = internalSession.Session;
        var response = await surrealSession.RawQuery(sql, null, ct).ConfigureAwait(false);

        if (response.HasErrors || response.Count == 0) return;

        List<EventRow>? eventRows = null;
        try
        {
            eventRows = response.GetValue<List<EventRow>>(0);
        }
        catch { return; }

        if (eventRows is null || eventRows.Count == 0) return;

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        // Group events by stream and replay apply logic
        var streamGroups = eventRows
            .Where(r => !string.IsNullOrEmpty(r.DataJson))
            .GroupBy(r => r.StreamId)
            .ToList();

        foreach (var group in streamGroups)
        {
            var events = new List<IEvent>();
            foreach (var row in group)
            {
                if (string.IsNullOrEmpty(row.DataJson)) continue;
                var eventType = EventTypes.FirstOrDefault(t => t.Name == row.EventType);
                if (eventType is null) continue;

                try
                {
                    var data = JsonSerializer.Deserialize(row.DataJson, eventType, jsonOptions);
                    if (data is not null)
                    {
                        events.Add((IEvent)Activator.CreateInstance(
                            typeof(Event<>).MakeGenericType(eventType),
                            [data, row.Version, 0L, row.CreatedAt, row.StreamId, Guid.Empty])!);
                    }
                }
                catch
                {
                    // Skip events that fail to deserialize
                }
            }

            if (events.Count > 0)
            {
                // Clear existing data for this stream's document before replay
                var docId = _getDocumentId(events.Select(e => e.Data).ToList().AsReadOnly());
                if (docId is not null)
                {
                    var docIdString = docId.ToString();
                    if (!string.IsNullOrEmpty(docIdString))
                    {
                        var storageTable = MetadataDispatch.GetTableName(typeof(TDoc));
                        await session.ExecuteSqlAsync($"DELETE {storageTable}:`{docIdString.Replace("'", "\\'")}`;", null, ct).ConfigureAwait(false);
                    }
                }

                var context = new ProjectionContext(session, events.AsReadOnly());
                await ApplyAsync(context, ct).ConfigureAwait(false);
            }
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static object? GetEventPropertyValue(object eventData, LambdaExpression expression)
    {
        try
        {
            var compiled = expression.Compile();
            return compiled.DynamicInvoke(eventData);
        }
        catch
        {
            return null;
        }
    }

    private static void SetPropertyValue(TDoc doc, LambdaExpression expression, object? value)
    {
        try
        {
            // Unwrap Convert expressions (e.g., value type to object)
            var body = expression.Body;
            while (body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
                body = unary.Operand;

            if (body is MemberExpression member)
            {
                var prop = member.Member as PropertyInfo;
                if (prop is not null && prop.CanWrite)
                {
                    var converted = ConvertValue(value, prop.PropertyType);
                    prop.SetValue(doc, converted);
                }
            }
        }
        catch
        {
            // Skip mapping errors silently
        }
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null) return null;
        if (targetType.IsInstanceOfType(value)) return value;

        try
        {
            return Convert.ChangeType(value, targetType);
        }
        catch
        {
            return value;
        }
    }

    // ─── Column mapping record ────────────────────────────────────────

    private sealed class ColumnMapping
    {
        public Type EventType { get; }
        public LambdaExpression? EventProperty { get; }
        public LambdaExpression? DocumentProperty { get; }
        public Func<object, object>? CustomValueProvider { get; set; }
        public Func<object, bool>? DeletePredicate { get; set; }

        public ColumnMapping(Type eventType, LambdaExpression? eventProperty, LambdaExpression? documentProperty)
        {
            EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
            EventProperty = eventProperty;
            DocumentProperty = documentProperty;
        }
    }
}
