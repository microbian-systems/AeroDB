namespace AeroDB.Sable;

/// <summary>
/// Controls how event data is serialized in the event store.
/// </summary>
public enum EventSerializationMode
{
    /// <summary>JSON serialization (default). Human-readable, compatible.</summary>
    Json = 0,

    /// <summary>Binary serialization (UTF-8 JSON bytes). Smaller, faster.</summary>
    Binary = 1
}
