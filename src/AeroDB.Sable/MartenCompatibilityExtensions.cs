using JasperFx.CodeGeneration;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AeroDB.Sable;

public enum EnumStorage
{
    AsString,
    AsInteger
}

public static class CombGuidIdGeneration
{
    public static Guid NewGuid() => Guid.CreateVersion7();
}

public static class MartenCompatibilityExtensions
{
    public static bool Contains(this IEnumerable<string> streamIds, Guid streamId)
        => streamIds.Contains(streamId.ToString());

    public static bool Contains(this IEnumerable<EventStreamIdentity> streamIds, Guid streamId)
        => streamIds.Any(id => id == streamId);


    public static StoreOptions UseSystemTextJsonForSerialization(
        this StoreOptions options,
        EnumStorage enumStorage = EnumStorage.AsString)
    {
        // Set the central EnumStorage config so all paths (JSON, LINQ, patching) agree
        options.EnumStorage = enumStorage;

        options.ConfigureSerializer(o =>
        {
            if (enumStorage == EnumStorage.AsString)
            {
                o.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            }
        });

        return options;
    }

    public static IServiceCollection OptimizeArtifactWorkflow(
        this IServiceCollection services,
        TypeLoadMode typeLoadMode)
        => services;

    public static IServiceCollection UseLightweightSessions(this IServiceCollection services)
        => services;

    public static IServiceCollection AddAsyncDaemon(
        this IServiceCollection services,
        DaemonMode mode)
        => services;

    public static IHostBuilder ApplyJasperFxExtensions(this IHostBuilder builder)
        => builder;

    public static async Task<int> RunJasperFxCommands(this IHost host, string[] args)
    {
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
