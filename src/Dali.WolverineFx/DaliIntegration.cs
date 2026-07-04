using AeroDB;
using AeroDB.WolverineFx.Codegen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Dali.WolverineFx;

/// <summary>
/// Wolverine extension that integrates AeroDB (SurrealDB) as the message persistence
/// and saga storage provider. Registers codegen sources, persistence frame provider,
/// and the AeroDB transport.
/// </summary>
public sealed class DaliIntegration : IWolverineExtension
{
    /// <summary>
    /// Register AeroDB-specific code generation sources, persistence strategy,
    /// and transport with the Wolverine options.
    /// </summary>
    public void Configure(WolverineOptions options)
    {
        options.CodeGeneration.Sources.Add(new DaliBackedPersistenceMarker());

        options.ScopingFrameSources.Add(() => new PrimeScopedDocumentSessionFrame());

        options.CodeGeneration.InsertFirstPersistenceStrategy<DaliPersistenceFrameProvider>();

        options.CodeGeneration.Sources.Add(new DaliOutboxedSessionFactorySource());

        var transport = options.Transports.GetOrCreate<DaliTransport>();

        options.Services.AddSingleton<ISagaStoreDiagnostics>(sp =>
            new DaliSagaStoreDiagnostics(
                sp.GetRequiredService<IWolverineRuntime>(),
                sp.GetRequiredService<IDocumentStore>()));

        options.Services.AddScoped<ScopedDocumentSessionHolder>();

        options.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAncillaryStoreFrameProvider, DaliAncillaryStoreFrameProvider>());

        options.Policies.Add(new DaliOpPolicy());

        // WolverineEnvelopeSchemas can be registered as an IConfigureDali by users
        // in their AddDali(options => opts.Configurators.Add(new WolverineEnvelopeSchemas()))
        // callback. DaliMessageStore handles its own schema creation internally via
        // SchemaManager in InitializeSchemaAsync().

        // Register DaliEventForwarding so it's available in the DI container for store configurators
        options.Services.TryAddSingleton<DaliEventForwarding>();
    }
}
