using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Dali.Metadata;

namespace Dali;

/// <summary>
/// Fluent builder for SurrealDB search queries (full-text, vector KNN, hybrid RRF fusion).
/// Obtained via <c>session.Search&lt;T&gt;()</c>.
/// Builds SurrealQL with LET variables internally, matching the pattern used by
/// <see cref="SearchExtensions"/> for direct extension-method usage.
/// </summary>
public interface ISearchQuery<T> where T : class
{
    /// <summary>
    /// Adds a full-text search condition on a single field with a given weight.
    /// Weight determines relevance scoring; higher = more important.
    /// </summary>
    ISearchQuery<T> MatchText(Expression<Func<T, object>> fieldSelector, double weight, string query);

    /// <summary>
    /// Adds a full-text search condition across multiple weighted fields.
    /// </summary>
    ISearchQuery<T> MatchText(
        IReadOnlyList<(Expression<Func<T, object>> FieldSelector, double Weight)> fields,
        string query);

    /// <summary>
    /// Convenience: single-field full-text search with weight 1.0.
    /// </summary>
    ISearchQuery<T> MatchText(Expression<Func<T, object>> fieldSelector, string query);

    /// <summary>
    /// Sets the vector (embedding) for KNN vector search.
    /// Requires an HNSW or MTREE index on the field.
    /// </summary>
    ISearchQuery<T> WithVector(Expression<Func<T, float[]>> fieldSelector, float[] queryVector);

    /// <summary>
    /// Limits the number of results returned.
    /// </summary>
    ISearchQuery<T> Take(int limit);

    /// <summary>
    /// Sets the number of KNN candidates to explore (default 100).
    /// Higher values increase accuracy at the cost of performance.
    /// </summary>
    ISearchQuery<T> Candidates(int count);

    /// <summary>
    /// Executes the search and returns results.
    /// For text-only or vector-only searches, runs a single query.
    /// For hybrid (text + vector), runs separate sub-queries and fuses via RRF.
    /// </summary>
    Task<List<T>> ToListAsync(CancellationToken ct = default);

    /// <summary>
    /// Executes hybrid search by fusing full-text and vector results
    /// using SurrealDB's Reciprocal Rank Fusion (search::rrf).
    /// Only valid when both MatchText and WithVector have been configured.
    /// </summary>
    /// <param name="rrfK">RRF smoothness constant (k). Default 60.</param>
    /// <param name="rrfLimit">Maximum candidates per sub-query for RRF fusion. Default 80.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<T>> FuseAsync(int rrfK = 60, int rrfLimit = 80, CancellationToken ct = default);
}

/// <summary>
/// Fluent search query builder for SurrealDB. Not intended for direct construction;
/// use <c>session.Search&lt;T&gt;()</c> instead.
/// </summary>
public sealed class DaliSearchQuery<T> : ISearchQuery<T> where T : class
{
    private readonly SurrealQueryProvider _provider;
    private readonly string _table;

    // Accumulated state
    private readonly List<(string FieldName, double Weight, int Index)> _textFields = new();
    private string? _textQuery;
    private string? _vectorField;
    private float[]? _queryVector;
    private int _limit = 30;
    private int _candidates = 100;

    internal DaliSearchQuery(SurrealQueryProvider provider)
    {
        _provider = provider;
        _table = MetadataDispatch.GetTableName(typeof(T));
    }

    public ISearchQuery<T> MatchText(Expression<Func<T, object>> fieldSelector, double weight, string query)
    {
        var fieldName = GetMemberName(fieldSelector);
        _textFields.Add((fieldName, weight, _textFields.Count));
        _textQuery = query;
        return this;
    }

    public ISearchQuery<T> MatchText(
        IReadOnlyList<(Expression<Func<T, object>> FieldSelector, double Weight)> fields,
        string query)
    {
        foreach (var (fieldSelector, weight) in fields)
        {
            var fieldName = GetMemberName(fieldSelector);
            _textFields.Add((fieldName, weight, _textFields.Count));
        }
        _textQuery = query;
        return this;
    }

    public ISearchQuery<T> MatchText(Expression<Func<T, object>> fieldSelector, string query)
        => MatchText(fieldSelector, 1.0, query);

    public ISearchQuery<T> WithVector(Expression<Func<T, float[]>> fieldSelector, float[] queryVector)
    {
        _vectorField = GetMemberName(fieldSelector);
        _queryVector = queryVector;
        return this;
    }

    public ISearchQuery<T> Take(int limit)
    {
        _limit = limit > 0 ? limit : 30;
        return this;
    }

    public ISearchQuery<T> Candidates(int count)
    {
        _candidates = count > 0 ? count : 100;
        return this;
    }

    public Task<List<T>> ToListAsync(CancellationToken ct = default)
    {
        if (_textFields.Count > 0 && _vectorField is not null)
            return FuseAsync(rrfK: 60, rrfLimit: _candidates, ct);

        if (_vectorField is not null)
            return ExecuteVectorSearchAsync(ct);

        if (_textFields.Count > 0)
            return ExecuteFullTextSearchAsync(ct);

        throw new InvalidOperationException(
            "Search query requires at least one .MatchText() or .WithVector() call before executing.");
    }

    public async Task<List<T>> FuseAsync(int rrfK = 60, int rrfLimit = 80, CancellationToken ct = default)
    {
        if (_textFields.Count == 0)
            throw new InvalidOperationException("Hybrid search requires at least one .MatchText() call.");
        if (_vectorField is null || _queryVector is null)
            throw new InvalidOperationException("Hybrid search requires .WithVector() to be configured.");

        var rrfKValue = rrfK > 0 ? rrfK : 60;
        var rrfLimitValue = rrfLimit > 0 ? rrfLimit : 80;

        // Build full-text sub-query
        var ftWhereParts = new List<string>();
        var ftScoreParts = new List<string>();
        foreach (var (fieldName, weight, index) in _textFields)
        {
            ftWhereParts.Add($"{fieldName} @{index}@ '{EscapeSurql(_textQuery!)}'");
            ftScoreParts.Add($"(search::score({index}) * {weight})");
        }

        var ftWhere = string.Join(" OR ", ftWhereParts);
        var ftScoreExpr = ftScoreParts.Count == 1 ? $"search::score(0)" : string.Join(" + ", ftScoreParts);

        // Build vector sub-query
        var vecStr = "[" + string.Join(", ", _queryVector.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

        // Build combined SurrealQL with LET variables and RRF
        var surql = $"""
LET $ft = (
    SELECT *, {ftScoreExpr} AS _ft_score
    FROM `{_table}`
    WHERE {ftWhere}
    ORDER BY _ft_score DESC
    LIMIT {rrfLimitValue}
);
LET $vs = (
    SELECT *, vector::distance::knn() AS _distance
    FROM `{_table}`
    WHERE {_vectorField} <|{rrfLimitValue},{_candidates}|> {vecStr}
    ORDER BY _distance ASC
    LIMIT {rrfLimitValue}
);
RETURN search::rrf([$ft, $vs], {rrfKValue}, {rrfLimitValue});
""";

        return await ExecuteRawSearchAsync(surql, ct).ConfigureAwait(false);
    }

    private async Task<List<T>> ExecuteFullTextSearchAsync(CancellationToken ct)
    {
        var whereParts = new List<string>();
        var scoreParts = new List<string>();
        foreach (var (fieldName, weight, index) in _textFields)
        {
            whereParts.Add($"{fieldName} @{index}@ '{EscapeSurql(_textQuery!)}'");
            scoreParts.Add($"(search::score({index}) * {weight})");
        }

        var where = string.Join(" OR ", whereParts);
        var scoreExpr = scoreParts.Count == 1 ? $"search::score(0)" : string.Join(" + ", scoreParts);

        var surql = $"SELECT *, {scoreExpr} AS _score FROM `{_table}` WHERE {where} ORDER BY _score DESC LIMIT {_limit};";
        return await ExecuteRawSearchAsync(surql, ct).ConfigureAwait(false);
    }

    private async Task<List<T>> ExecuteVectorSearchAsync(CancellationToken ct)
    {
        var vecStr = "[" + string.Join(", ", _queryVector!.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

        var surql = $"SELECT *, vector::distance::knn() AS _distance FROM `{_table}` WHERE {_vectorField} <|{_limit},{_candidates}|> {vecStr} ORDER BY _distance ASC LIMIT {_limit};";
        return await ExecuteRawSearchAsync(surql, ct).ConfigureAwait(false);
    }

    private async Task<List<T>> ExecuteRawSearchAsync(string surql, CancellationToken ct)
    {
        var response = await _provider.Session.RawQuery(surql, null, ct).ConfigureAwait(false);

        if (!response.HasErrors && response.Count > 0)
        {
            var raw = response.GetValue<List<T>>(0);
            if (raw is not null) return raw;
        }

        return [];
    }

    private static string GetMemberName<TProp>(Expression<Func<T, TProp>> selector)
    {
        if (selector.Body is MemberExpression m)
            return m.Member.Name;
        if (selector.Body is UnaryExpression { NodeType: ExpressionType.Convert, Operand: MemberExpression um })
            return um.Member.Name;
        throw new ArgumentException("Selector must be a simple member expression");
    }

    private static string EscapeSurql(string value)
        => value.Replace("'", "\\'");
}

public static class SearchQueryExtensions
{
    /// <summary>
    /// Starts a search query against SurrealDB indexes.
    /// Supports full-text, vector KNN, and hybrid search with RRF fusion.
    /// </summary>
    /// <typeparam name="T">The document type to search.</typeparam>
    /// <param name="session">The query session.</param>
    /// <returns>A fluent search query builder.</returns>
    public static ISearchQuery<T> Search<T>(this IQuerySession session) where T : class
    {
        var queryable = session.Query<T>();
        if (queryable.Provider is SurrealQueryProvider provider)
            return new DaliSearchQuery<T>(provider);

        throw new NotSupportedException(
            $"Search is only supported on Dali query sessions. The current provider is {queryable.Provider.GetType().Name}.");
    }
}
