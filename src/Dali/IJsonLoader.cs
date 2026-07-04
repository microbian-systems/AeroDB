namespace Dali;

/// <summary>Raw JSON document loading without deserialization.</summary>
public interface IJsonLoader
{
    /// <summary>Load a document as a raw JSON string by ID.</summary>
    Task<string?> LoadByIdAsync<T>(string id, CancellationToken ct = default) where T : class;

    /// <summary>Load a document as a raw JSON object by ID.</summary>
    Task<System.Text.Json.JsonDocument?> LoadDocumentByIdAsync<T>(string id, CancellationToken ct = default) where T : class;
}
