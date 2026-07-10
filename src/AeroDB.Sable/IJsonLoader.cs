namespace AeroDB.Sable;

using Microsoft.AspNetCore.Http;

/// <summary>Raw JSON document loading without deserialization.</summary>
public interface IJsonLoader
{
    /// <summary>Load a document as a raw JSON string by ID.</summary>
    Task<string?> LoadByIdAsync<T>(string id, CancellationToken ct = default) where T : class;

    /// <summary>Load a document as a raw JSON object by ID.</summary>
    Task<System.Text.Json.JsonDocument?> LoadDocumentByIdAsync<T>(string id, CancellationToken ct = default) where T : class;

    /// <summary>Write a single document by its object ID to the HTTP response.</summary>
    Task WriteById<T>(object id, HttpContext httpContext, CancellationToken ct = default) where T : class;

    /// <summary>Write a single document by its string ID to the HTTP response.</summary>
    Task WriteById<T>(string id, HttpContext httpContext, CancellationToken ct = default) where T : class
        => WriteById<T>((object)id, httpContext, ct);

    /// <summary>Write a single document by its integer ID to the HTTP response.</summary>
    Task WriteById<T>(int id, HttpContext httpContext, CancellationToken ct = default) where T : class
        => WriteById<T>((object)id, httpContext, ct);

    /// <summary>Write a single document by its long ID to the HTTP response.</summary>
    Task WriteById<T>(long id, HttpContext httpContext, CancellationToken ct = default) where T : class
        => WriteById<T>((object)id, httpContext, ct);

    /// <summary>Write a single document by its Guid ID to the HTTP response.</summary>
    Task WriteById<T>(Guid id, HttpContext httpContext, CancellationToken ct = default) where T : class
        => WriteById<T>((object)id, httpContext, ct);

    /// <summary>Query all documents of type T and write them as a JSON array to the HTTP response.</summary>
    Task WriteArray<T>(HttpContext httpContext, CancellationToken ct = default) where T : class;
}
