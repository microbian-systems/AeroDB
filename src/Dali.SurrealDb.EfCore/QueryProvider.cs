// ============================================================
// SurrealDB EF Core LINQ Provider
// IQueryProvider, IQueryContext, and SurrealDbSet<T>
// ============================================================

using System.Collections;
using System.Linq.Expressions;
using System.Text.Json;
using SurrealEFCore.Core;
using SurrealEFCore.Query;

namespace SurrealEFCore.Infrastructure;

// ── Query Execution Contract ──────────────────────────────────

public interface ISurrealQueryExecutor
{
    Task<IEnumerable<T>> ExecuteAsync<T>(TranslatedQuery query, CancellationToken ct = default);
    Task<T?> ExecuteScalarAsync<T>(TranslatedQuery query, CancellationToken ct = default);
}

// ── LINQ QueryProvider ────────────────────────────────────────

/// <summary>
/// The bridge between LINQ (IQueryable) and SurrealDB.
/// Translates expression trees → SurrealQL → HTTP execution.
/// </summary>
public class SurrealQueryProvider : IQueryProvider
{
    private readonly ISurrealQueryExecutor _executor;

    public SurrealQueryProvider(ISurrealQueryExecutor executor)
        => _executor = executor;

    // IQueryProvider implementation ────────────────────────────

    public IQueryable CreateQuery(Expression expression)
    {
        var elemType = TypeHelper.GetElementType(expression.Type)
            ?? throw new InvalidOperationException("Cannot infer element type.");
        return (IQueryable)Activator.CreateInstance(
            typeof(SurrealQueryable<>).MakeGenericType(elemType),
            this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new SurrealQueryable<TElement>(this, expression);

    public object? Execute(Expression expression)
        => ExecuteCore<object>(expression);

    public TResult Execute<TResult>(Expression expression)
        => ExecuteCore<TResult>(expression);

    // Sync wrapper (blocks; prefer async APIs) ─────────────────
    private TResult ExecuteCore<TResult>(Expression expression)
    {
        var visitor = new SurrealExpressionVisitor();
        var query   = visitor.Translate(expression);

        Console.WriteLine($"[SurrealQL] {query.SurrealQL}");
        Console.WriteLine($"[Mode]      {query.Mode}");

        var result = _executor
            .ExecuteAsync<TResult>(query)
            .GetAwaiter()
            .GetResult();

        return result.FirstOrDefault()!;
    }

    // Async execution ──────────────────────────────────────────

    public async Task<List<T>> ToListAsync<T>(
        Expression expression,
        CancellationToken ct = default)
    {
        var visitor = new SurrealExpressionVisitor();
        var query   = visitor.Translate(expression);

        Console.WriteLine($"[SurrealQL] {query.SurrealQL}");
        Console.WriteLine($"[Mode]      {query.Mode}");

        var results = await _executor.ExecuteAsync<T>(query, ct);
        return results.ToList();
    }

    public async Task<T?> FirstOrDefaultAsync<T>(
        Expression expression,
        CancellationToken ct = default)
    {
        var visitor = new SurrealExpressionVisitor();
        var query   = visitor.Translate(expression);

        Console.WriteLine($"[SurrealQL] {query.SurrealQL}");
        return await _executor.ExecuteScalarAsync<T>(query, ct);
    }
}

// ── IQueryable<T> implementation ──────────────────────────────

/// <summary>Represents a SurrealDB table as a LINQ-queryable set.</summary>
public class SurrealQueryable<T> : IQueryable<T>, IAsyncEnumerable<T>
{
    private readonly SurrealQueryProvider _provider;

    public SurrealQueryable(SurrealQueryProvider provider)
    {
        _provider   = provider;
        Expression  = Expression.Constant(this);
        ElementType = typeof(T);
    }

    public SurrealQueryable(SurrealQueryProvider provider, Expression expression)
    {
        _provider   = provider;
        Expression  = expression;
        ElementType = typeof(T);
    }

    public Type            ElementType { get; }
    public Expression      Expression  { get; }
    public IQueryProvider  Provider    => _provider;

    public IEnumerator<T> GetEnumerator()
    {
        var results = _provider.ToListAsync<T>(Expression).GetAwaiter().GetResult();
        return results.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
    {
        var results = await _provider.ToListAsync<T>(Expression, ct);
        foreach (var item in results)
            yield return item;
    }

    // Async extension shortcuts ────────────────────────────────
    public Task<List<T>> ToListAsync(CancellationToken ct = default)
        => _provider.ToListAsync<T>(Expression, ct);

    public Task<T?> FirstOrDefaultAsync(CancellationToken ct = default)
        => _provider.FirstOrDefaultAsync<T>(Expression, ct);
}

// ── DbSet-like facade ─────────────────────────────────────────

/// <summary>
/// High-level set that matches EF Core's DbSet API surface
/// for familiarity, wrapping SurrealQueryable.
/// </summary>
public class SurrealDbSet<T> : SurrealQueryable<T>
    where T : SurrealEntity
{
    private readonly SurrealContext _ctx;
    private readonly string         _tableName;

    public SurrealDbSet(SurrealContext ctx, SurrealQueryProvider provider)
        : base(provider)
    {
        _ctx       = ctx;
        _tableName = typeof(T).Name.ToLower();
    }

    // ── CRUD ──────────────────────────────────────────────────

    public async Task<T> AddAsync(T entity, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entity.Id.Id))
            entity.Id = new RecordId(_tableName, Guid.NewGuid().ToString("N"));

        var surql = $"CREATE {entity.Id} CONTENT {Serialize(entity)};";
        await _ctx.ExecuteRawAsync(surql, ct);
        return entity;
    }

    public async Task<T?> FindAsync(RecordId id, CancellationToken ct = default)
    {
        var surql = $"SELECT * FROM {id};";
        return await _ctx.QuerySingleAsync<T>(surql, ct);
    }

    public async Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        var surql = $"UPDATE {entity.Id} CONTENT {Serialize(entity)};";
        await _ctx.ExecuteRawAsync(surql, ct);
    }

    public async Task DeleteAsync(RecordId id, CancellationToken ct = default)
    {
        await _ctx.ExecuteRawAsync($"DELETE {id};", ct);
    }

    // ── Upsert ───────────────────────────────────────────────

    public async Task<T> UpsertAsync(T entity, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entity.Id.Id))
            entity.Id = new RecordId(_tableName, Guid.NewGuid().ToString("N"));

        var surql = $"UPSERT {entity.Id} CONTENT {Serialize(entity)};";
        await _ctx.ExecuteRawAsync(surql, ct);
        return entity;
    }

    private static string Serialize(T entity) =>
        JsonSerializer.Serialize(entity, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
}

// ── Type helpers ──────────────────────────────────────────────

internal static class TypeHelper
{
    public static Type? GetElementType(Type type)
    {
        if (type.IsGenericType)
            foreach (var arg in type.GetGenericArguments())
                return arg;
        return null;
    }
}
