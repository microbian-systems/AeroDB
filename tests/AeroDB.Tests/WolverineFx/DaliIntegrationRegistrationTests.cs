namespace AeroDB.Tests;

using AeroDB.WolverineFx;
using AeroDB.WolverineFx.Codegen;
using global::AeroDB;
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
/// Pure unit tests for <see cref="DaliIntegration.Configure"/>.
/// Verifies that the correct services are registered in the WolverineOptions
/// service collection. No SurrealDB, no Wolverine runtime — only options + NSubstitute.
/// </summary>
public class DaliIntegrationRegistrationTests
{
#pragma warning disable TUnit0023 // _options doesn't implement IDisposable — false positive
    private readonly WolverineOptions _options;
#pragma warning restore TUnit0023
    private readonly DaliIntegration _integration;

    public DaliIntegrationRegistrationTests()
    {
        _options = new WolverineOptions();
        _integration = new DaliIntegration();
    }

    // ====================================================================
    // DaliBackedPersistenceMarker — registered as codegen source
    // ====================================================================

    [Test]
    public void Registers_DaliBackedPersistenceMarker_AsCodegenSource()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        _options.CodeGeneration.Sources
            .ShouldContain(s => s is DaliBackedPersistenceMarker);
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
    // DaliPersistenceFrameProvider — inserted as first persistence strategy
    // ====================================================================

    [Test]
    public void Registers_DaliPersistenceFrameProvider_AsFirstPersistenceStrategy()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        // The code generation stores a list of persistence providers in its Properties dictionary.
        // PersistenceProviders() extension method retrieves them.
        var providers = _options.CodeGeneration.PersistenceProviders();
        providers[0].ShouldBeOfType<DaliPersistenceFrameProvider>();
    }

    // ====================================================================
    // DaliOutboxedSessionFactorySource — registered as codegen source
    // ====================================================================

    [Test]
    public void Registers_DaliOutboxedSessionFactorySource_AsCodegenSource()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        _options.CodeGeneration.Sources
            .ShouldContain(s => s is DaliOutboxedSessionFactorySource);
    }

    // ====================================================================
    // DaliTransport — created / registered via options.Transports
    // ====================================================================

    [Test]
    public void Registers_DaliTransport()
    {
        // Act
        _integration.Configure(_options);

        // Assert: the AeroDB transport should be in the transports collection
        var transport = _options.Transports
            .OfType<DaliTransport>()
            .FirstOrDefault();

        transport.ShouldNotBeNull();
        transport.Protocol.ShouldBe("dali");
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
    // DaliAncillaryStoreFrameProvider — registered as singleton
    // ====================================================================

    [Test]
    public void Registers_DaliAncillaryStoreFrameProvider()
    {
        // Act
        _integration.Configure(_options);

        // Assert: registered via TryAddEnumerable as IAncillaryStoreFrameProvider
        var registration = _options.Services
            .FirstOrDefault(s =>
                s.ServiceType == typeof(IAncillaryStoreFrameProvider) &&
                s.ImplementationType == typeof(DaliAncillaryStoreFrameProvider));

        registration.ShouldNotBeNull();
        registration.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    // ====================================================================
    // DaliOpPolicy — added to policies collection
    // ====================================================================

    [Test]
    public void Registers_DaliOpPolicy()
    {
        // Act
        _integration.Configure(_options);

        // Assert: DaliOpPolicy is internal, so we check by type name via policies
        var policy = _options.Policies
            .OfType<object>()
            .FirstOrDefault(p => p.GetType().Name == "DaliOpPolicy");

        policy.ShouldNotBeNull();
    }

    // ====================================================================
    // DaliEventForwarding — registered as singleton
    // ====================================================================

    [Test]
    public void Registers_DaliEventForwarding_AsSingleton()
    {
        // Act
        _integration.Configure(_options);

        // Assert
        var registration = _options.Services
            .FirstOrDefault(s => s.ServiceType == typeof(DaliEventForwarding));

        registration.ShouldNotBeNull();
        registration.Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    // ====================================================================
    // DaliSagaStoreDiagnostics — registered as singleton with factory
    // ====================================================================

    [Test]
    public void Registers_DaliSagaStoreDiagnostics_AsSingleton()
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
        // We verify the code doesn't throw and that AeroDB transport is still unique.
        Should.NotThrow(() => _integration.Configure(_options));

        var transports = _options.Transports.OfType<DaliTransport>().ToArray();
        transports.Length.ShouldBe(1);
    }
}
