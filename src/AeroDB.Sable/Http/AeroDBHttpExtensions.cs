using Microsoft.AspNetCore.Http;

namespace AeroDB.Sable;

/// <summary>
/// ASP.NET streaming helper extensions on <see cref="IQuerySession"/>.
/// Provides Marten-compatible <c>WriteById&lt;T&gt;</c> and <c>WriteArray&lt;T&gt;</c>
/// methods for writing documents directly to HTTP responses.
/// </summary>
public static class AeroDBHttpExtensions
{
    /// <summary>
    /// Loads a single document by its string ID and writes it as JSON to the HTTP response.
    /// Sets status to 404 if the document is not found.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="id">The document ID (string form, e.g. <c>"person:john"</c>).</param>
    /// <param name="httpContext">The HTTP context to write the response to.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteById<T>(
        this IQuerySession session,
        string id,
        HttpContext httpContext,
        CancellationToken ct = default)
        where T : class
        => await session.Json.WriteById<T>(id, httpContext, ct).ConfigureAwait(false);

    /// <summary>
    /// Loads a single document by its <c>long</c> ID and writes it as JSON to the HTTP response.
    /// Sets status to 404 if the document is not found.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="id">The document ID (long form).</param>
    /// <param name="httpContext">The HTTP context to write the response to.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteById<T>(
        this IQuerySession session,
        long id,
        HttpContext httpContext,
        CancellationToken ct = default)
        where T : class
        => await session.Json.WriteById<T>(id, httpContext, ct).ConfigureAwait(false);

    /// <summary>
    /// Loads a single document by its <c>Guid</c> ID and writes it as JSON to the HTTP response.
    /// Sets status to 404 if the document is not found.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="id">The document ID (Guid form).</param>
    /// <param name="httpContext">The HTTP context to write the response to.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteById<T>(
        this IQuerySession session,
        Guid id,
        HttpContext httpContext,
        CancellationToken ct = default)
        where T : class
        => await session.Json.WriteById<T>(id, httpContext, ct).ConfigureAwait(false);

    /// <summary>
    /// Queries all documents of type <typeparamref name="T"/> and writes the result
    /// as a JSON array to the HTTP response.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="httpContext">The HTTP context to write the response to.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteArray<T>(
        this IQuerySession session,
        HttpContext httpContext,
        CancellationToken ct = default)
        where T : class
        => await session.Query<T>().WriteArray(httpContext, ct).ConfigureAwait(false);

    /// <summary>
    /// Writes the results of a SurrealDB query as a JSON array to the HTTP response.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="queryable">The query to execute.</param>
    /// <param name="httpContext">The HTTP context to write the response to.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteArray<T>(
        this ISurrealDbQueryable<T> queryable,
        HttpContext httpContext,
        CancellationToken ct = default)
        where T : class
    {
        var results = await queryable.ToListAsync(ct).ConfigureAwait(false);
        httpContext.Response.ContentType = "application/json";
        await System.Text.Json.JsonSerializer
            .SerializeAsync(httpContext.Response.Body, results, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the results of a LINQ query as a JSON array to the HTTP response when backed by AeroDB.Sable.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="queryable">The query to execute.</param>
    /// <param name="httpContext">The HTTP context to write the response to.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteArray<T>(
        this IQueryable<T> queryable,
        HttpContext httpContext,
        CancellationToken ct = default)
        where T : class
    {
        if (queryable is not ISurrealDbQueryable<T> surrealQueryable)
        {
            throw new NotSupportedException("WriteArray is only supported on ISurrealDbQueryable<T> queries.");
        }

        await surrealQueryable.WriteArray(httpContext, ct).ConfigureAwait(false);
    }
}
