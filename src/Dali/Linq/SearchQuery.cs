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
    /// Applies a filter condition to the search results.
    /// The expression is translated to a SurrealQL WHERE clause
    /// using the existing SurrealExpressionVisitor.
    /// </summary>
    ISearchQuery<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary>
    /// Orders the search results by the specified property.
    /// Supports ascending and descending.
    /// </summary>
    ISearchQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector, bool descending = false);

    /// <summary>
    /// Skips the specified number of results (for pagination).
    /// </summary>
    ISearchQuery<T> Skip(int count);

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

    // Filtering, ordering, pagination state
    private Expression<Func<T, bool>>? _wherePredicate;
    private string? _whereClause;
    private string? _orderByClause;
    private int _skip;

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

    public ISearchQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        _wherePredicate = predicate;
        _whereClause = SurrealExpressionVisitor.TranslateCondition(predicate.Body);
        return this;
    }

    public ISearchQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector, bool descending = false)
    {
        var fieldName = GetMemberName(keySelector);
        _orderByClause = descending ? $"{fieldName} DESC" : $"{fieldName} ASC";
        return this;
    }

    public ISearchQuery<T> Skip(int count)
    {
        _skip = count > 0 ? count : 0;
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

        // Add extra WHERE filter to FTS sub-query
        var ftWhereClause = _whereClause is not null
            ? $"({ftWhere}) AND ({_whereClause})"
            : ftWhere;

        // Build vector sub-query
        var vecStr = "[" + string.Join(", ", _queryVector.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

        // Add extra WHERE filter to vector sub-query
        var vecExtraWhere = _whereClause is not null
            ? $" AND ({_whereClause})"
            : "";

        // ORDER BY on each sub-query (user override for FTS, always _distance for KNN)
        var ftOrderBy = _orderByClause ?? "_ft_score DESC";

        // Build SurrealQL LET blocks
        var surql = $"""
LET $ft = (
    SELECT *, {ftScoreExpr} AS _ft_score
    FROM `{_table}`
    WHERE {ftWhereClause}
    ORDER BY {ftOrderBy}
    LIMIT {rrfLimitValue}
);
LET $vs = (
    SELECT *, vector::distance::knn() AS _distance
    FROM `{_table}`
    WHERE {_vectorField} <|{rrfLimitValue},{_candidates}|> {vecStr}{vecExtraWhere}
    ORDER BY _distance ASC
    LIMIT {rrfLimitValue}
);
""";

        // Apply ORDER BY / SKIP on RRF result when user specified extras
        bool hasFinalExtras = _orderByClause is not null || _skip > 0;
        if (hasFinalExtras)
        {
            var finalOrderBy = _orderByClause ?? "_ft_score DESC";
            var startAt = _skip > 0 ? $" START AT {_skip}" : "";
            surql += $"RETURN (SELECT * FROM search::rrf([$ft, $vs], {rrfKValue}, {rrfLimitValue}) ORDER BY {finalOrderBy} LIMIT {_limit}{startAt});";
        }
        else
        {
            surql += $"RETURN search::rrf([$ft, $vs], {rrfKValue}, {rrfLimitValue});";
        }

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

        var ftsWhere = string.Join(" OR ", whereParts);
        var scoreExpr = scoreParts.Count == 1 ? $"search::score(0)" : string.Join(" + ", scoreParts);

        // Compose WHERE: combine FTS condition with optional extra filter
        var whereClause = _whereClause is not null
            ? $"({ftsWhere}) AND ({_whereClause})"
            : ftsWhere;

        // Compose ORDER BY: use extra ordering or fall back to score
        var orderBy = _orderByClause ?? "_score DESC";

        // Compose LIMIT + START AT for pagination
        var startAt = _skip > 0 ? $" START AT {_skip}" : "";

        var surql = $"SELECT *, {scoreExpr} AS _score FROM `{_table}` WHERE {whereClause} ORDER BY {orderBy} LIMIT {_limit}{startAt};";
        return await ExecuteRawSearchAsync(surql, ct).ConfigureAwait(false);
    }

    private async Task<List<T>> ExecuteVectorSearchAsync(CancellationToken ct)
    {
        var vecStr = "[" + string.Join(", ", _queryVector!.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

        bool hasExtras = _whereClause is not null || _orderByClause is not null || _skip > 0;

        if (hasExtras)
        {
            // KNN <|K,N|> imposes a hard limit.  Increase K so outer query
            // can apply extra WHERE filter, ORDER BY override, and SKIP.
            var knnInnerLimit = _skip + _limit;

            var subSurql = $"SELECT *, vector::distance::knn() AS _distance FROM `{_table}` WHERE {_vectorField} <|{knnInnerLimit},{_candidates}|> {vecStr}";

            var wherePart = _whereClause is not null ? $" WHERE {_whereClause}" : "";
            var orderBy = _orderByClause ?? "_distance ASC";
            var startAt = _skip > 0 ? $" START AT {_skip}" : "";

            var surql = $"SELECT * FROM ({subSurql}) AS knn_sub{wherePart} ORDER BY {orderBy} LIMIT {_limit}{startAt};";
            return await ExecuteRawSearchAsync(surql, ct).ConfigureAwait(false);
        }

        var surqlSimple = $"SELECT *, vector::distance::knn() AS _distance FROM `{_table}` WHERE {_vectorField} <|{_limit},{_candidates}|> {vecStr} ORDER BY _distance ASC LIMIT {_limit};";
        return await ExecuteRawSearchAsync(surqlSimple, ct).ConfigureAwait(false);
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
