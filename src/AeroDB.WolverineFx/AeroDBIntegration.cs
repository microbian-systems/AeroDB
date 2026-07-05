using AeroDB;
using AeroDB.WolverineFx.Codegen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx;

/// <summary>
/// Wolverine extension that integrates AeroDB (SurrealDB) as the message persistence
/// and saga storage provider. Registers codegen sources, persistence frame provider,
/// and the AeroDB transport.
/// </summary>
public sealed class AeroDBIntegration : IWolverineExtension
{
    /// <summary>
    /// Register AeroDB-specific code generation sources, persistence strategy,
    /// and transport with the Wolverine options.
    /// </summary>
    public void Configure(WolverineOptions options)
    {
        options.CodeGeneration.Sources.Add(new AeroDBBackedPersistenceMarker());

        options.ScopingFrameSources.Add(() => new PrimeScopedDocumentSessionFrame());

        options.CodeGeneration.InsertFirstPersistenceStrategy<AeroDBPersistenceFrameProvider>();

        options.CodeGeneration.Sources.Add(new AeroDBOutboxedSessionFactorySource());

        var transport = options.Transports.GetOrCreate<AeroDBTransport>();

        options.Services.AddSingleton<ISagaStoreDiagnostics>(sp =>
            new AeroDBSagaStoreDiagnostics(
                sp.GetRequiredService<IWolverineRuntime>(),
                sp.GetRequiredService<IDocumentStore>()));

        options.Services.AddScoped<ScopedDocumentSessionHolder>();

        options.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAncillaryStoreFrameProvider, AeroDBAncillaryStoreFrameProvider>());

        options.Policies.Add(new AeroDBOpPolicy());

        // WolverineEnvelopeSchemas can be registered as an IConfigureAeroDB by users
        // in their AddAeroDB(options => opts.Configurators.Add(new WolverineEnvelopeSchemas()))
        // callback. AeroDBMessageStore handles its own schema creation internally via
        // SchemaManager in InitializeSchemaAsync().

        // Register AeroDBEventForwarding so it's available in the DI container for store configurators
        options.Services.TryAddSingleton<AeroDBEventForwarding>();
    }
}
