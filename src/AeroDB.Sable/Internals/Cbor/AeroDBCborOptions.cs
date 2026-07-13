using System.Text.Json;
using Dahomey.Cbor;
using SurrealDb.Net.Internals.Cbor;
using SurrealDb.Net;

namespace AeroDB.Sable.Internals.Cbor;

internal static class AeroDBCborOptions
{
    public static CborOptions GetCborSerializerOptions()
    {
        return SurrealDbCborOptions.GetCborSerializerOptions(Configure);
    }

    public static void Configure(CborOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Registry.ConverterRegistry.RegisterConverter(
            typeof(DateTimeOffset),
            new DateTimeOffsetSurrogateConverter());

        options.Registry.ConverterRegistry.RegisterConverter(
            typeof(GeometryPoint),
            new GeometryPointSurrogateConverter());

        options.Registry.ConverterRegistry.RegisterConverter(
            typeof(GeometryPolygon),
            new GeometryPolygonSurrogateConverter());

        options.Registry.ConverterRegistry.RegisterConverter(
            typeof(JsonElement),
            new JsonElementCborConverter());
    }

    public static void ConfigureClient(ISurrealDbClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        // SurrealDbMemoryClient exposes the configureCborOptions constructor only internally.
        // Patch the already-created engine so factory-created embedded clients get the same
        // DateTimeOffset converter as AeroDB.Sable-created SurrealDbClient instances.
        object? engine = null;
        for (var type = client.GetType(); type is not null; type = type.BaseType)
        {
            var engineProperty = type.GetProperty(
                "Engine",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            if (engineProperty is not null)
            {
                engine = engineProperty.GetValue(client);
                break;
            }
        }

        if (engine is null)
        {
            return;
        }

        var configureField = engine.GetType()
            .GetField("_configureCborOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        if (configureField?.FieldType != typeof(Action<CborOptions>))
        {
            return;
        }

        Action<CborOptions> configure = Configure;
        var existing = (Action<CborOptions>?)configureField.GetValue(engine);
        if (existing is null)
        {
            configureField.SetValue(engine, configure);
            return;
        }

        if (existing.GetInvocationList().Any(d => d.Method == configure.Method && d.Target == configure.Target))
        {
            return;
        }

        configureField.SetValue(engine, existing + configure);
    }
}
