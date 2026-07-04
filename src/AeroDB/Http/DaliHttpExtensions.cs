using Microsoft.AspNetCore.Http;

namespace AeroDB;

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
    /// <param name="httpContext">The HTTP context to write the response to.</param>
    /// <param name="id">The document ID (string form, e.g. <c>"person:john"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteById<T>(
        this IQuerySession session,
        HttpContext httpContext,
        string id,
        CancellationToken ct = default)
        where T : class
    {
        var doc = await session.LoadAsync<T>(id, ct).ConfigureAwait(false);
        if (doc is null)
        {
            httpContext.Response.StatusCode = 404;
            return;
        }

        httpContext.Response.ContentType = "application/json";
        await System.Text.Json.JsonSerializer
            .SerializeAsync(httpContext.Response.Body, doc, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a single document by its <c>long</c> ID and writes it as JSON to the HTTP response.
    /// Sets status to 404 if the document is not found.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="httpContext">The HTTP context to write the response to.</param>
    /// <param name="id">The document ID (long form).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteById<T>(
        this IQuerySession session,
        HttpContext httpContext,
        long id,
        CancellationToken ct = default)
        where T : class
    {
        var doc = await session.LoadAsync<T>(id, ct).ConfigureAwait(false);
        if (doc is null)
        {
            httpContext.Response.StatusCode = 404;
            return;
        }

        httpContext.Response.ContentType = "application/json";
        await System.Text.Json.JsonSerializer
            .SerializeAsync(httpContext.Response.Body, doc, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a single document by its <c>Guid</c> ID and writes it as JSON to the HTTP response.
    /// Sets status to 404 if the document is not found.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="session">The query session.</param>
    /// <param name="httpContext">The HTTP context to write the response to.</param>
    /// <param name="id">The document ID (Guid form).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task WriteById<T>(
        this IQuerySession session,
        HttpContext httpContext,
        Guid id,
        CancellationToken ct = default)
        where T : class
    {
        var doc = await session.LoadAsync<T>(id, ct).ConfigureAwait(false);
        if (doc is null)
        {
            httpContext.Response.StatusCode = 404;
            return;
        }

        httpContext.Response.ContentType = "application/json";
        await System.Text.Json.JsonSerializer
            .SerializeAsync(httpContext.Response.Body, doc, cancellationToken: ct)
            .ConfigureAwait(false);
    }

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
    {
        var results = await session.Query<T>().ToListAsync(ct).ConfigureAwait(false);
        httpContext.Response.ContentType = "application/json";
        await System.Text.Json.JsonSerializer
            .SerializeAsync(httpContext.Response.Body, results, cancellationToken: ct)
            .ConfigureAwait(false);
    }
}
