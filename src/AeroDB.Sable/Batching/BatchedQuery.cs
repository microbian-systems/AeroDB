using AeroDB.Sable.Metadata;
using SurrealDb.Net.Models.Response;

namespace AeroDB.Sable;

/// <summary>
/// Internal batch query implementation. Constructed by
/// <see cref="InternalSessionBase.CreateBatchQuery"/>.
/// </summary>
internal class BatchedQuery : IBatchedQuery
{
    private readonly InternalSessionBase _session;
    private readonly List<BatchItem> _compiledItems = new();
    private readonly List<SimpleBatchItem> _simpleItems = new();

    internal BatchedQuery(InternalSessionBase session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Events = new BatchEvents(this);
    }

    /// <inheritdoc />
    public Task<TOut> Query<TDoc, TOut>(ICompiledQuery<TDoc, TOut> compiledQuery)
        where TDoc : class
    {
        ThrowIfEncrypted<TDoc>();
        ArgumentNullException.ThrowIfNull(compiledQuery);

        var plan = CompiledQueryPlanner.GetOrBuildPlan<TDoc, TOut>(compiledQuery);
        var tcs = new TaskCompletionSource<TOut>(TaskCreationOptions.RunContinuationsAsynchronously);

        void SetResult(SurrealDbResponse response, int index)
        {
            try
            {
                // Check for errors at this result index
                if (index < 0 || index >= response.Count)
                {
                    tcs.TrySetException(new IndexOutOfRangeException(
                        $"Batch result index {index} is out of range (count: {response.Count})"));
                    return;
                }

                if (response[index] is ISurrealDbErrorResult)
                {
                    var message = response[index] switch
                    {
                        SurrealDbErrorResult e => e.Details,
                        SurrealDbProtocolErrorResult p => $"{p.Details} ({p.Description})",
                        _ => "Batch query statement failed"
                    };
                    tcs.TrySetException(new InvalidOperationException(
                        $"Batch query statement at index {index} failed: {message}"));
                    return;
                }

                var list = _session.DeserializeMappedPocoResponse<TDoc>(response, index);

                if (plan.IsSingleResult)
                {
                    var first = list.Count > 0 ? list[0] : default;
                    tcs.TrySetResult((TOut)(object?)first!);
                }
                else
                {
                    // TOut is expected to be IEnumerable<TDoc> or similar collection
                    tcs.TrySetResult((TOut)(object)list);
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        _compiledItems.Add(new BatchItem(compiledQuery, plan, SetResult));
        return tcs.Task;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> QueryRawAsync(string surql, CancellationToken ct = default)
    {
        var response = await _session.Session.RawQuery(surql, null, ct).ConfigureAwait(false);
        var results = new List<string>();
        for (var i = 0; i < response.Count; i++)
        {
            var raw = response.GetValue<List<object>>(i);
            if (raw is { Count: > 0 })
            {
                foreach (var item in raw)
                    results.Add(item?.ToString() ?? "");
            }
        }
        return results.AsReadOnly();
    }

    // ── IBatchedQuery (Marten API parity) ─────────────────────────

    /// <inheritdoc />
    public IBatchedQuery CheckExists<T>(string id, out Task<bool> result) where T : class
    {
        ThrowIfEncrypted<T>();
        var table = MetadataDispatch.GetTableName(typeof(T));
        var surql = $"SELECT id FROM {table}:`{id.Replace("`", "\\`")}` LIMIT 1;";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _simpleItems.Add(new SimpleBatchItem(surql, (response, index) =>
        {
            try
            {
                var exists = response.Count > 0 && !response.HasErrors
                    && index < response.Count
                    && response[index] is not ISurrealDbErrorResult;
                tcs.TrySetResult(exists);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }));
        result = tcs.Task;
        return this;
    }

    /// <inheritdoc />
    public IBatchedQuery Load<T>(string id, out Task<T?> result) where T : class
    {
        ThrowIfEncrypted<T>();
        var table = MetadataDispatch.GetTableName(typeof(T));
        var surql = $"SELECT * FROM {table}:`{id.Replace("`", "\\`")}`;";
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _simpleItems.Add(new SimpleBatchItem(surql, (response, index) =>
        {
            try
            {
                if (response.Count <= index || response[index] is ISurrealDbErrorResult)
                {
                    tcs.TrySetResult(default);
                    return;
                }
                var list = response.GetValue<List<T>>(index);
                tcs.TrySetResult(list is { Count: > 0 } ? list[0] : default);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }));
        result = tcs.Task;
        return this;
    }

    /// <inheritdoc />
    public IBatchedQuery LoadMany<T>(IEnumerable<string> ids, out Task<IReadOnlyList<T>> result) where T : class
    {
        ThrowIfEncrypted<T>();
        var idList = ids?.ToList() ?? throw new ArgumentNullException(nameof(ids));
        var table = MetadataDispatch.GetTableName(typeof(T));
        var inClause = string.Join(", ", idList.Select(id => $"'{table}:{id}'"));
        var surql = $"SELECT * FROM {table} WHERE id IN [{inClause}];";
        var tcs = new TaskCompletionSource<IReadOnlyList<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _simpleItems.Add(new SimpleBatchItem(surql, (response, index) =>
        {
            try
            {
                if (response.Count <= index || response[index] is ISurrealDbErrorResult)
                {
                    tcs.TrySetResult(Array.Empty<T>());
                    return;
                }
                var list = response.GetValue<List<T>>(index);
                tcs.TrySetResult(list?.AsReadOnly() ?? (IReadOnlyList<T>)Array.Empty<T>());
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }));
        result = tcs.Task;
        return this;
    }

    /// <inheritdoc />
    public IBatchedQuery Query<T>(out Task<IReadOnlyList<T>> result) where T : class
    {
        ThrowIfEncrypted<T>();
        var table = MetadataDispatch.GetTableName(typeof(T));
        var surql = $"SELECT * FROM {table};";
        var tcs = new TaskCompletionSource<IReadOnlyList<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _simpleItems.Add(new SimpleBatchItem(surql, (response, index) =>
        {
            try
            {
                if (response.Count <= index || response[index] is ISurrealDbErrorResult)
                {
                    tcs.TrySetResult(Array.Empty<T>());
                    return;
                }
                var list = response.GetValue<List<T>>(index);
                tcs.TrySetResult(list?.AsReadOnly() ?? (IReadOnlyList<T>)Array.Empty<T>());
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }));
        result = tcs.Task;
        return this;
    }

    /// <inheritdoc />
    public IBatchedQuery AddItem<T>(Func<IReadOnlyList<T>, Task> handler) where T : class
    {
        // AddItem is reserved for post-batch custom processing and does not
        // add a SQL statement. The handler is invoked during Execute() after
        // all SQL statements complete. It receives an empty list — it is
        // intended for correlation with other batch operations.
        _simpleItems.Add(new SimpleBatchItem("", (_, _) =>
        {
            // No-op during response processing; handled after full execute
        }));
        // Store the handler separately for after-execute invocation
        _postBatchHandlers.Add(handler as Delegate);
        return this;
    }

    /// <inheritdoc />
    public IBatchedQuery QueryByPlan<T>(string plan, out Task<IReadOnlyList<T>> result) where T : class
    {
        ThrowIfEncrypted<T>();
        var surql = plan.EndsWith(';') ? plan : plan + ";";
        var tcs = new TaskCompletionSource<IReadOnlyList<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _simpleItems.Add(new SimpleBatchItem(surql, (response, index) =>
        {
            try
            {
                if (response.Count <= index || response[index] is ISurrealDbErrorResult)
                {
                    tcs.TrySetResult(Array.Empty<T>());
                    return;
                }
                var list = response.GetValue<List<T>>(index);
                tcs.TrySetResult(list?.AsReadOnly() ?? (IReadOnlyList<T>)Array.Empty<T>());
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }));
        result = tcs.Task;
        return this;
    }

    /// <inheritdoc />
    public IBatchEvents Events { get; }

    /// <inheritdoc />
    public IQuerySession Parent => (IQuerySession)_session;

    // Holds AddItem handlers for post-execute invocation
    private readonly List<Delegate> _postBatchHandlers = new();

    /// <inheritdoc />
    public async Task Execute(CancellationToken ct = default)
    {
        if (_compiledItems.Count == 0 && _simpleItems.Count == 0)
            return;

        var sqlParts = new List<string>();
        var compiledActive = new List<BatchItem>();
        var simpleActive = new List<SimpleBatchItem>();

        foreach (var item in _compiledItems)
        {
            var queryResult = BuildSurrealQueryResult(item);
            var surql = queryResult.ToSurrealQL();

            // Inline parameters so each statement is self-contained
            if (queryResult.Parameters is { Count: > 0 })
                surql = InlineParameters(surql, queryResult.Parameters);

            if (!string.IsNullOrEmpty(surql))
            {
                sqlParts.Add(surql);
                compiledActive.Add(item);
            }
        }

        foreach (var item in _simpleItems)
        {
            if (!string.IsNullOrEmpty(item.Surql))
            {
                sqlParts.Add(item.Surql);
                simpleActive.Add(item);
            }
        }

        if (sqlParts.Count == 0)
            return;

        // Join all statements — each already ends with ';' from ToSurrealQL() or inline
        var combined = string.Join("\n", sqlParts);
        var response = await _session.Session.RawQuery(combined, null, ct)
            .ConfigureAwait(false);

        int index = 0;
        foreach (var item in compiledActive)
        {
            item.SetResult(response, index);
            index++;
        }
        foreach (var item in simpleActive)
        {
            item.SetResult(response, index);
            index++;
        }

        // Invoke post-batch AddItem handlers (only for non-empty results)
        // Handlers are stored in order of AddItem calls
        // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
        var handlerIndex = 0;
        foreach (var item in simpleActive)
        {
            if (handlerIndex < _postBatchHandlers.Count)
            {
                _postBatchHandlers[handlerIndex].DynamicInvoke(response);
                handlerIndex++;
            }
        }
    }

    /// <summary>
    /// Clones the plan's skeleton, substitutes live property values
    /// (including LIMIT/START overrides), and returns the populated
    /// <see cref="SurrealQueryResult"/> ready for <c>ToSurrealQL()</c>.
    /// </summary>
    private static SurrealQueryResult BuildSurrealQueryResult(BatchItem item)
    {
        var plan = item.Plan;
        var query = item.CompiledQuery;
        var result = plan.SkeletonResult.Clone();

        // Substitute LIMIT / START from actual property values
        if (plan.LimitProperty is not null)
        {
            var limitProp = plan.Properties.First(p => p.Name == plan.LimitProperty);
            result.Limit = (int)limitProp.GetValue(query)!;
        }

        if (plan.SkipProperty is not null)
        {
            var skipProp = plan.Properties.First(p => p.Name == plan.SkipProperty);
            result.Skip = (int)skipProp.GetValue(query)!;
        }

        // Build a new parameter dictionary with live property values
        var newParams = new Dictionary<string, object?>(plan.ParameterMapping.Count);
        foreach (var kvp in plan.ParameterMapping)
        {
            var prop = plan.Properties.First(p => p.Name == kvp.Value);
            newParams[kvp.Key] = prop.GetValue(query);
        }
        result.Parameters = newParams;

        // Apply LIMIT 1 for single-result queries
        if (plan.IsSingleResult)
            result.Limit = 1;

        return result;
    }

    /// <summary>
    /// Replaces SurrealQL parameter placeholders (<c>$p0</c>, <c>$p1</c>, …)
    /// with their literal values so the SQL is self-contained and multiple
    /// statements with overlapping parameter names don't collide.
    /// </summary>
    private static string InlineParameters(
        string surql,
        IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return surql;

        foreach (var kvp in parameters)
        {
            var placeholder = "$" + kvp.Key;
            var value = FormatInlineValue(kvp.Value);
            surql = surql.Replace(placeholder, value);
        }

        return surql;
    }

    /// <summary>
    /// Formats a runtime value as a SurrealQL literal (inline).
    /// Mirrors the logic in <see cref="SurrealExpressionVisitor"/>.
    /// </summary>
    private static string FormatInlineValue(object? value)
    {
        return value switch
        {
            null => "NONE",
            string s => $"'{s.Replace("'", "\\'")}'",
            bool b => b ? "true" : "false",
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            short s => s.ToString(System.Globalization.CultureInfo.InvariantCulture),
            byte b => b.ToString(System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => $"d'{dt:yyyy-MM-ddTHH:mm:ssZ}'",
            DateTimeOffset dto => $"d'{dto:yyyy-MM-ddTHH:mm:ssZ}'",
            _ => $"'{value}'"
        };
    }

    private void ThrowIfEncrypted<T>() where T : class
    {
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), _session.StoreOptions.Schema))
            throw new SableEncryptedOperationNotSupportedException(typeof(T), "batched query");
    }

    /// <summary>
    /// Holds a compiled query, its cached plan, and a closure that sets the
    /// result on a <see cref="TaskCompletionSource{TOut}"/> after execution.
    /// </summary>
    private sealed class BatchItem
    {
        public object CompiledQuery { get; }
        public CompiledPlan Plan { get; }
        public Action<SurrealDbResponse, int> SetResult { get; }

        public BatchItem(
            object compiledQuery,
            CompiledPlan plan,
            Action<SurrealDbResponse, int> setResult)
        {
            CompiledQuery = compiledQuery;
            Plan = plan;
            SetResult = setResult;
        }
    }

    /// <summary>
    /// Holds a raw SurrealQL statement and a result callback for simple
    /// (non-compiled-query) batch operations.
    /// </summary>
    private sealed class SimpleBatchItem
    {
        public string Surql { get; }
        public Action<SurrealDbResponse, int> SetResult { get; }

        public SimpleBatchItem(string surql, Action<SurrealDbResponse, int> setResult)
        {
            Surql = surql;
            SetResult = setResult;
        }
    }

    /// <summary>
    /// Inner class that implements <see cref="IBatchEvents"/> for queuing
    /// event-fetch operations within a batch.
    /// </summary>
    private sealed class BatchEvents : IBatchEvents
    {
        private readonly BatchedQuery _owner;

        public BatchEvents(BatchedQuery owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public IBatchedQuery FetchStream(string streamId, out Task<IReadOnlyList<IEvent>> result)
        {
            var safeStreamId = streamId.Replace("'", "\\'");
            var surql = $"SELECT * FROM mt_events WHERE stream_id = '{safeStreamId}' ORDER BY sequence ASC;";
            var tcs = new TaskCompletionSource<IReadOnlyList<IEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _owner._simpleItems.Add(new SimpleBatchItem(surql, (response, index) =>
            {
                try
                {
                    if (response.Count <= index || response[index] is ISurrealDbErrorResult)
                    {
                        tcs.TrySetResult(Array.Empty<IEvent>());
                        return;
                    }
                    var list = response.GetValue<List<IEvent>>(index);
                    tcs.TrySetResult(list?.AsReadOnly() ?? (IReadOnlyList<IEvent>)Array.Empty<IEvent>());
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }));
            result = tcs.Task;
            return _owner;
        }

        public IBatchedQuery FetchAllAfterSequence(long sequence, out Task<IReadOnlyList<IEvent>> result)
        {
            var surql = $"SELECT * FROM mt_events WHERE sequence > {sequence} ORDER BY sequence ASC;";
            var tcs = new TaskCompletionSource<IReadOnlyList<IEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _owner._simpleItems.Add(new SimpleBatchItem(surql, (response, index) =>
            {
                try
                {
                    if (response.Count <= index || response[index] is ISurrealDbErrorResult)
                    {
                        tcs.TrySetResult(Array.Empty<IEvent>());
                        return;
                    }
                    var list = response.GetValue<List<IEvent>>(index);
                    tcs.TrySetResult(list?.AsReadOnly() ?? (IReadOnlyList<IEvent>)Array.Empty<IEvent>());
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }));
            result = tcs.Task;
            return _owner;
        }
    }
}
