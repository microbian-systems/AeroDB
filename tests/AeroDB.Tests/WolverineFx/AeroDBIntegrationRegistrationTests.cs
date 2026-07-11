using AeroDB.WolverineFx;
using AeroDB.WolverineFx.Codegen;

namespace AeroDB.Tests;

using AeroDB.WolverineFx;
using AeroDB.WolverineFx.Codegen;
using global::AeroDB.Sable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

/// <summary>
/// Pure unit tests for <see cref="AeroDBIntegration.Configure"/>.
/// Verifies that the correct services are registered in the WolverineOptions
/// service collection. No SurrealDB, no Wolverine runtime — only options + NSubstitute.
/// </summary>
public class AeroDBIntegrationRegistrationTests
{
#pragma warning disable TUnit0023 // _options doesn't implement IDisposable — false positive
    private readonly WolverineOptions _options;
#pragma warning restore TUnit0023
    private readonly AeroDBIntegration _integration;

    public AeroDBIntegrationRegistrationTests()
    {
        _options = new WolverineOptions();
        _integration = new AeroDBIntegration();
    }

    // ====================================================================
    // AeroDBBackedPersistenceMarker — registered as codegen source
    // ====================================================================

    [Test]
    public void Registers_AeroDBBackedPersistenceMarker_AsCodegenSource()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        _options.CodeGeneration.Sources
            .ShouldContain(s => s is AeroDBBackedPersistenceMarker);
    }

    // ====================================================================
    // PrimeScopedDocumentSessionFrame — registered as scoping frame source
    // ====================================================================

    [Test]
    public void Registers_PrimeScopedDocumentSessionFrame_AsScopingFrameSource()
    {
        // Act
        _integration.Configure(_options);

        // Assert: the factory should produce an PrimeScopedDocumentSessionFrame
        var frameSourceFactories = _options.ScopingFrameSources;
        var frames = frameSourceFactories.Select(f => f()).ToArray();
        frames.ShouldContain(f => f is PrimeScopedDocumentSessionFrame);
    }

    // ====================================================================
    // AeroDBPersistenceFrameProvider — inserted as first persistence strategy
    // ====================================================================

    [Test]
    public void Registers_AeroDBPersistenceFrameProvider_AsFirstPersistenceStrategy()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        // The code generation stores a list of persistence providers in its Properties dictionary.
        // PersistenceProviders() extension method retrieves them.
        var providers = _options.CodeGeneration.PersistenceProviders();
        providers[0].ShouldBeOfType<AeroDBPersistenceFrameProvider>();
    }

    // ====================================================================
    // AeroDBOutboxedSessionFactorySource — registered as codegen source
    // ====================================================================

    [Test]
    public void Registers_AeroDBOutboxedSessionFactorySource_AsCodegenSource()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        _options.CodeGeneration.Sources
            .ShouldContain(s => s is AeroDBOutboxedSessionFactorySource);
    }

    // ====================================================================
    // AeroDBTransport — created / registered via options.Transports
    // ====================================================================

    [Test]
    public void Registers_AeroDBTransport()
    {
        // Act
        _integration.Configure(_options);

        // Assert: the AeroDB.Sable transport should be in the transports collection
        var transport = _options.Transports
            .OfType<AeroDBTransport>()
            .FirstOrDefault();

        transport.ShouldNotBeNull();
        transport.Protocol.ShouldBe("AeroDB.Sable");
    }

    // ====================================================================
    // ScopedDocumentSessionHolder — registered as scoped
    // ====================================================================

    [Test]
    public void Registers_ScopedDocumentSessionHolder_AsScoped()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        var registration = _options.Services
            .FirstOrDefault(s => s.ServiceType == typeof(ScopedDocumentSessionHolder));

        registration.ShouldNotBeNull();
        registration.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    // ====================================================================
    // AeroDBAncillaryStoreFrameProvider — registered as singleton
    // ====================================================================

    [Test]
    public void Registers_AeroDBAncillaryStoreFrameProvider()
    {
        // Act
        _integration.Configure(_options);

        // Assert: registered via TryAddEnumerable as IAncillaryStoreFrameProvider
        var registration = _options.Services
            .FirstOrDefault(s =>
                s.ServiceType == typeof(IAncillaryStoreFrameProvider) &&
                s.ImplementationType == typeof(AeroDBAncillaryStoreFrameProvider));

        registration.ShouldNotBeNull();
        registration.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    // ====================================================================
    // AeroDBOpPolicy — added to policies collection
    // ====================================================================

    [Test]
    public void Registers_AeroDBOpPolicy()
    {
        // Act
        _integration.Configure(_options);

        // Assert: AeroDBOpPolicy is internal, so we check by type name via policies
        var policy = _options.Policies
            .OfType<object>()
            .FirstOrDefault(p => p.GetType().Name == "AeroDBOpPolicy");

        policy.ShouldNotBeNull();
    }

    // ====================================================================
    // AeroDBEventForwarding — registered as singleton
    // ====================================================================

    [Test]
    public void Registers_AeroDBEventForwarding_AsSingleton()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        var registration = _options.Services
            .FirstOrDefault(s => s.ServiceType == typeof(AeroDBEventForwarding));

        registration.ShouldNotBeNull();
        registration.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    // ====================================================================
    // AeroDBSagaStoreDiagnostics — registered as singleton with factory
    // ====================================================================

    [Test]
    public void Registers_AeroDBSagaStoreDiagnostics_AsSingleton()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        var registration = _options.Services
            .FirstOrDefault(s => s.ServiceType == typeof(ISagaStoreDiagnostics));

        registration.ShouldNotBeNull();
        registration.Lifetime.ShouldBe(ServiceLifetime.Singleton);
        registration.ImplementationFactory.ShouldNotBeNull();
    }

    // ====================================================================
    // Full registration smoke test — no exceptions thrown
    // ====================================================================

    [Test]
    public void Configure_DoesNotThrow()
    {
        // Act & Assert
        Should.NotThrow(() => _integration.Configure(_options));
    }

    // ====================================================================
    // Multiple calls are idempotent — does not duplicate
    // ====================================================================

    [Test]
    public void Configure_CalledTwice_DoesNotDuplicateRegistrations()
    {
        // Act
        _integration.Configure(_options);
        _integration.Configure(_options);

        // Assert: TryAdd pattern keeps singleton registrations from duplicating.
        // We verify the code doesn't throw and that AeroDB.Sable transport is still unique.
        Should.NotThrow(() => _integration.Configure(_options));

        var transports = _options.Transports.OfType<AeroDBTransport>().ToArray();
        transports.Length.ShouldBe(1);
    }
}
