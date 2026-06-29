using SurrealDb.Net.Models.Response;

namespace Dali;

/// <summary>
/// Internal batch query implementation. Constructed by
/// <see cref="InternalSessionBase.CreateBatchQuery"/>.
/// </summary>
internal class BatchedQuery : IBatchedQuery
{
    private readonly InternalSessionBase _session;
    private readonly List<BatchItem> _items = new();

    internal BatchedQuery(InternalSessionBase session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public Task<TOut> Query<TDoc, TOut>(ICompiledQuery<TDoc, TOut> compiledQuery)
        where TDoc : class
    {
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

                var list = response.GetValue<List<TDoc>>(index) ?? new List<TDoc>();

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

        _items.Add(new BatchItem(compiledQuery, plan, SetResult));
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

    /// <inheritdoc />
    public async Task Execute(CancellationToken ct = default)
    {
        if (_items.Count == 0)
            return;

        var sqlParts = new List<string>();
        var activeItems = new List<BatchItem>();

        foreach (var item in _items)
        {
            var queryResult = BuildSurrealQueryResult(item);
            var surql = queryResult.ToSurrealQL();

            // Inline parameters so each statement is self-contained
            if (queryResult.Parameters is { Count: > 0 })
                surql = InlineParameters(surql, queryResult.Parameters);

            if (!string.IsNullOrEmpty(surql))
            {
                sqlParts.Add(surql);
                activeItems.Add(item);
            }
        }

        if (sqlParts.Count == 0)
            return;

        // Join all statements — each already ends with ';' from ToSurrealQL()
        var combined = string.Join("\n", sqlParts);
        var response = await _session.Session.RawQuery(combined, null, ct)
            .ConfigureAwait(false);

        for (int i = 0; i < activeItems.Count; i++)
        {
            activeItems[i].SetResult(response, i);
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
}
