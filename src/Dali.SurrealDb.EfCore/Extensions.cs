// ============================================================
// SurrealDB EF Core LINQ Provider
// Extension Methods: Graph Traversal, Async, Raw SQL
// ============================================================

using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using SurrealEFCore.Core;
using SurrealEFCore.Infrastructure;

namespace SurrealEFCore.Extensions;

// ── Graph Traversal LINQ Extension ───────────────────────────

public static class SurrealGraphExtensions
{
    /// <summary>
    /// Extend a LINQ query with a graph traversal step.
    ///
    /// Usage:
    ///   ctx.People
    ///      .Where(p => p.Name == "Alice")
    ///      .Traverse&lt;Knows, Person&gt;("out")
    ///      .Where(p => p.Age > 18)
    ///      .ToListAsync();
    ///
    /// Emits:
    ///   SELECT ->knows->person.* FROM person
    ///   WHERE name = 'Alice' AND age > 18
    /// </summary>
    public static IQueryable<TTarget> Traverse<TEdge, TTarget>(
        this IQueryable<SurrealEntity> source,
        string direction = "out")
        where TEdge   : SurrealEntity
        where TTarget : SurrealEntity
    {
        var method = typeof(SurrealGraphExtensions)
            .GetMethod(nameof(Traverse))!
            .MakeGenericMethod(typeof(TEdge), typeof(TTarget));

        var call = Expression.Call(
            method,
            source.Expression,
            Expression.Constant(direction));

        return source.Provider.CreateQuery<TTarget>(call);
    }
}

// ── Raw SurrealQL Escape Hatch ────────────────────────────────

public static class SurrealRawExtensions
{
    /// <summary>
    /// Inject raw SurrealQL into a WHERE clause.
    ///
    /// Usage:
    ///   ctx.People.Raw("age > math::max(18, 21)").ToListAsync();
    ///
    /// Emits:
    ///   SELECT * FROM person WHERE /* raw */ age > math::max(18, 21)
    /// </summary>
    public static IQueryable<T> Raw<T>(
        this IQueryable<T> source,
        string surrealQL)
    {
        var method = typeof(SurrealRawExtensions)
            .GetMethod(nameof(Raw))!
            .MakeGenericMethod(typeof(T));

        var call = Expression.Call(
            method,
            source.Expression,
            Expression.Constant(surrealQL));

        return source.Provider.CreateQuery<T>(call);
    }
}

// ── Async LINQ Operators ──────────────────────────────────────

public static class SurrealAsyncExtensions
{
    public static Task<List<T>> ToListAsync<T>(
        this IQueryable<T> source,
        CancellationToken ct = default)
    {
        if (source is SurrealQueryable<T> sq)
            return sq.ToListAsync(ct);

        // Fallback for non-Surreal providers (testing)
        return Task.FromResult(source.ToList());
    }

    public static Task<T?> FirstOrDefaultAsync<T>(
        this IQueryable<T> source,
        CancellationToken ct = default)
    {
        if (source is SurrealQueryable<T> sq)
            return sq.FirstOrDefaultAsync(ct);

        return Task.FromResult(source.FirstOrDefault());
    }

    public static async Task<T?> FirstOrDefaultAsync<T>(
        this IQueryable<T> source,
        Expression<Func<T, bool>> predicate,
        CancellationToken ct = default)
        => await source.Where(predicate).FirstOrDefaultAsync(ct);

    public static async Task<bool> AnyAsync<T>(
        this IQueryable<T> source,
        Expression<Func<T, bool>> predicate,
        CancellationToken ct = default)
    {
        var result = await source.Where(predicate).FirstOrDefaultAsync(ct);
        return result is not null;
    }

    public static async Task<int> CountAsync<T>(
        this IQueryable<T> source,
        CancellationToken ct = default)
    {
        var list = await source.ToListAsync(ct);
        return list.Count;
    }
}

// ── DI / ServiceCollection Registration ───────────────────────

public static class SurrealServiceExtensions
{
    /// <summary>
    /// Register SurrealContext with DI.
    ///
    /// Usage in Program.cs:
    ///   builder.Services.AddSurrealDb(opts => {
    ///       opts.Url       = "http://localhost:8000";
    ///       opts.Namespace = "myns";
    ///       opts.Database  = "mydb";
    ///   });
    /// </summary>
    public static IServiceCollection AddSurrealDb(
        this IServiceCollection services,
        Action<SurrealDbOptions> configure)
    {
        var options = new SurrealDbOptions();
        configure(options);

        services.AddSingleton(options);
        services.AddScoped<SurrealContext>();

        return services;
    }
}

// ── Shim so the file compiles standalone ──────────────────────
public interface IServiceCollection
{
    IServiceCollection AddSingleton<T>(T instance) where T : class;
    IServiceCollection AddScoped<T>() where T : class;
}
