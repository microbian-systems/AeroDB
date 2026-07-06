---
title: Custom Serialization
description: Custom JSON serialization in AeroDB
---

## Default Serialization

AeroDB uses `System.Text.Json` with camelCase naming and case-insensitive deserialization. Properties like `FirstName` serialize to `firstName` — no attributes required for basic POCOs.

## Customizing Serializer Options

Override at the store level:

```csharp
var store = Documents.For(opts =>
{
    opts.Connection("http://localhost:8000", "ns", "db");
    opts.SerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
});
```

Use `ConfigureSerializer` for fine-grained control. Changes affect all document types — use `[JsonConverter]` attributes for per-type control.

## SurrealDB-Specific Type Mappings

| SurrealDB Type | .NET Type |
|----------------|-----------|
| `record` | `RecordId` |
| `datetime` | `DateTime` (UTC) |
| `geometry` | `GeometryPoint` / `GeometryPolygon` |
| `decimal` | `decimal` |
| `duration` | `TimeSpan` |
| `uuid` | `Guid` |

```csharp
public class LocationRecord
{
    public RecordId Id { get; set; }
    public GeometryPoint Coordinates { get; set; }
    public DateTime CapturedAt { get; set; }
}
```

## Custom Type Converters

Implement `JsonConverter<T>` for domain types:

```csharp
public class EmailAddressConverter : JsonConverter<EmailAddress>
{
    public override EmailAddress? Read(ref Utf8JsonReader reader, Type type,
        JsonSerializerOptions opts) => EmailAddress.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, EmailAddress value,
        JsonSerializerOptions opts) => writer.WriteStringValue(value.ToString());
}

opts.SerializerOptions.Converters.Add(new EmailAddressConverter());
```

## Polymorphic Types

Use `[JsonPolymorphic]` with a discriminator for correct deserialization:

```csharp
[JsonPolymorphic(TypeDiscriminatorProperty = "$type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ImageBlock), "image")]
public abstract class ContentBlock { }
```

## Binary Data and Version-Tolerant Serialization

Store BLOBs as `byte[]` (base64). For > 1 MB, use external storage with a URL reference.

Preserve unknown fields during schema evolution with `[JsonExtensionData]`:

```csharp
public class UserV2
{
    public string Name { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
```

Unknown fields round-trip safely through `ExtensionData` during rolling deployments.
