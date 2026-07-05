namespace AeroDB.Tests;

using System.Reflection;
using AeroDB.WolverineFx;
using AeroDB.WolverineFx.Codegen;
using global::AeroDB;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using NSubstitute;
using Shouldly;
using TUnit.Core;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;

/// <summary>
/// Pure unit tests for <see cref="AeroDBPersistenceFrameProvider"/>.
/// No Wolverine runtime, no SurrealDB — only NSubstitute mocks.
/// </summary>
public class AeroDBPersistenceFrameProviderTests
{
    private readonly AeroDBPersistenceFrameProvider _provider = new();
    private readonly IServiceContainer _container = Substitute.For<IServiceContainer>();

    // ====================================================================
    // CanApply — SagaChain
    // ====================================================================

    [Test]
    public void CanApply_ReturnsTrue_ForSagaChain()
    {
        // Arrange: create a minimal SagaChain via the (HandlerCall, HandlerGraph, Endpoint[]) ctor
        var sagaChain = CreateMinimalSagaChain();
        var container = Substitute.For<IServiceContainer>();

        // Act
        var result = _provider.CanApply(sagaChain, container);

        // Assert
        result.ShouldBeTrue();
    }

    // ====================================================================
    // CanApply — Non-SagaChain with / without IDocumentSession
    // ====================================================================

    [Test]
    public void CanApply_ReturnsTrue_ForChainWithIDocumentSessionDependency()
    {
        // Arrange
        var chain = Substitute.For<IChain>();
        chain.Middleware.Returns([]);
        chain.Postprocessors.Returns([]);
        chain.ServiceDependencies(_container, Arg.Is<IReadOnlyList<Type>>(l =>
            l.Contains(typeof(IDocumentSession)) || l.Contains(typeof(IQuerySession))))
            .Returns([typeof(IDocumentSession)]);

        // Act
        var result = _provider.CanApply(chain, _container);

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public void CanApply_ReturnsFalse_ForChainWithoutAeroDBDependencies()
    {
        // Arrange
        var chain = Substitute.For<IChain>();
        chain.Middleware.Returns([]);
        chain.Postprocessors.Returns([]);
        chain.ServiceDependencies(_container, Arg.Is<IReadOnlyList<Type>>(l =>
            l.Contains(typeof(IDocumentSession)) || l.Contains(typeof(IQuerySession))))
            .Returns([]);

        // Act
        var result = _provider.CanApply(chain, _container);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void CanApply_ReturnsFalse_WhenOnlyIQuerySessionDependency()
    {
        // Arrange: IQuerySession alone should NOT trigger AeroDB persistence
        var chain = Substitute.For<IChain>();
        chain.Middleware.Returns([]);
        chain.Postprocessors.Returns([]);
        chain.ServiceDependencies(_container, Arg.Is<IReadOnlyList<Type>>(l =>
            l.Contains(typeof(IDocumentSession)) || l.Contains(typeof(IQuerySession))))
            .Returns([typeof(IQuerySession)]);

        // Act
        var result = _provider.CanApply(chain, _container);

        // Assert
        result.ShouldBeFalse();
    }

    // ====================================================================
    // ApplyTransactionSupport — non-SagaChain
    // ====================================================================

    [Test]
    public void ApplyTransactionSupport_ForNonSagaChain_AddsOpenAeroDBSessionMiddleware()
    {
        // Arrange
        var chain = Substitute.For<IChain>();
        chain.Middleware.Returns([]);
        chain.Postprocessors.Returns([]);

        // Act
        _provider.ApplyTransactionSupport(chain, _container);

        // Assert
        chain.Middleware.ShouldContain(f => f is OpenAeroDBSessionFrame);
    }

    [Test]
    public void ApplyTransactionSupport_ForNonSagaChain_AddsSaveChangesPostProcessor()
    {
        // Arrange
        var chain = Substitute.For<IChain>();
        chain.Middleware.Returns([]);
        chain.Postprocessors.Returns([]);

        // Act
        _provider.ApplyTransactionSupport(chain, _container);

        // Assert
        chain.Postprocessors.ShouldContain(f => f is AeroDBSessionSaveChangesFrame);
    }

    [Test]
    public void ApplyTransactionSupport_ForNonSagaChain_AddsFlushOutgoingPostProcessor()
    {
        // Arrange
        var chain = Substitute.For<IChain>();
        chain.Middleware.Returns([]);
        chain.Postprocessors.Returns([]);

        // Act
        _provider.ApplyTransactionSupport(chain, _container);

        // Assert
        chain.Postprocessors.ShouldContain(f => f is FlushAeroDBOutgoingMessagesFrame);
    }

    [Test]
    public void ApplyTransactionSupport_ForNonSagaChain_DoesNotDuplicateMiddleware()
    {
        // Arrange: pre-add an OpenAeroDBSessionFrame
        var chain = Substitute.For<IChain>();
        var middleware = new List<Frame> { new OpenAeroDBSessionFrame(Substitute.For<IChain>()) };
        chain.Middleware.Returns(middleware);
        chain.Postprocessors.Returns([]);

        // Act
        _provider.ApplyTransactionSupport(chain, _container);

        // Assert
        chain.Middleware.Count(f => f is OpenAeroDBSessionFrame).ShouldBe(1);
    }

    [Test]
    public void ApplyTransactionSupport_ForNonSagaChain_DoesNotDuplicatePostprocessors()
    {
        // Arrange: pre-add both post-processors
        var chain = Substitute.For<IChain>();
        chain.Middleware.Returns([]);
        var postprocessors = new List<Frame>
        {
            new AeroDBSessionSaveChangesFrame(),
            new FlushAeroDBOutgoingMessagesFrame()
        };
        chain.Postprocessors.Returns(postprocessors);

        // Act
        _provider.ApplyTransactionSupport(chain, _container);

        // Assert: no duplicates added
        chain.Postprocessors.Count(f => f is AeroDBSessionSaveChangesFrame).ShouldBe(1);
        chain.Postprocessors.Count(f => f is FlushAeroDBOutgoingMessagesFrame).ShouldBe(1);
    }

    // ====================================================================
    // ApplyTransactionSupport — SagaChain (should only get middleware, no postprocessors)
    // ====================================================================

    [Test]
    public void ApplyTransactionSupport_ForSagaChain_DoesNotAddSaveChangesPostProcessor()
    {
        // Arrange
        var sagaChain = CreateMinimalSagaChain();
        var initialPostCount = sagaChain.Postprocessors.Count;

        // Act
        _provider.ApplyTransactionSupport(sagaChain, _container);

        // Assert: no AeroDBSessionSaveChangesFrame or FlushAeroDBOutgoingMessagesFrame added
        sagaChain.Postprocessors.ShouldNotContain(f => f is AeroDBSessionSaveChangesFrame);
        sagaChain.Postprocessors.ShouldNotContain(f => f is FlushAeroDBOutgoingMessagesFrame);
    }

    [Test]
    public void ApplyTransactionSupport_ForSagaChain_AddsOpenAeroDBSessionMiddleware()
    {
        // Arrange
        var sagaChain = CreateMinimalSagaChain();

        // Act
        _provider.ApplyTransactionSupport(sagaChain, _container);

        // Assert
        sagaChain.Middleware.ShouldContain(f => f is OpenAeroDBSessionFrame);
    }

    // ====================================================================
    // ApplyTransactionSupport overload with entityType
    // ====================================================================

    [Test]
    public void ApplyTransactionSupport_WithEntityType_DelegatesToBaseMethod()
    {
        // Arrange
        var chain = Substitute.For<IChain>();
        chain.Middleware.Returns([]);
        chain.Postprocessors.Returns([]);

        // Act
        _provider.ApplyTransactionSupport(chain, _container, typeof(object));

        // Assert: same frames added as the non-entity overload
        chain.Middleware.ShouldContain(f => f is OpenAeroDBSessionFrame);
        chain.Postprocessors.ShouldContain(f => f is AeroDBSessionSaveChangesFrame);
        chain.Postprocessors.ShouldContain(f => f is FlushAeroDBOutgoingMessagesFrame);
    }

    // ====================================================================
    // DetermineSagaIdType
    // ====================================================================

    [Test]
    public void DetermineSagaIdType_ReturnsGuid()
    {
        // Act
        var idType = _provider.DetermineSagaIdType(typeof(object), _container);

        // Assert
        idType.ShouldBe(typeof(Guid));
    }

    // ====================================================================
    // CanPersist
    // ====================================================================

    [Test]
    public void CanPersist_ReturnsTrue_AndSetsPersistenceService()
    {
        // Act
        var result = _provider.CanPersist(typeof(object), _container, out var serviceType);

        // Assert
        result.ShouldBeTrue();
        serviceType.ShouldBe(typeof(IDocumentSession));
    }

    // ====================================================================
    // DetermineLoadFrame
    // ====================================================================

    [Test]
    public void DetermineLoadFrame_ReturnsLoadSagaFrame()
    {
        // Arrange
        var sagaId = new Variable(typeof(Guid), "sagaId");

        // Act
        var frame = _provider.DetermineLoadFrame(_container, typeof(object), sagaId);

        // Assert
        frame.ShouldBeOfType<LoadSagaFrame>();
    }

    // ====================================================================
    // DetermineInsertFrame
    // ====================================================================

    [Test]
    public void DetermineInsertFrame_ReturnsSagaOperationFrameWithStore()
    {
        // Arrange
        var saga = new Variable(typeof(object), "saga");

        // Act
        var frame = _provider.DetermineInsertFrame(saga, _container);

        // Assert
        frame.ShouldBeOfType<SagaOperationFrame>();
    }

    // ====================================================================
    // CommitUnitOfWorkFrame
    // ====================================================================

    [Test]
    public void CommitUnitOfWorkFrame_ReturnsAeroDBSessionSaveChangesFrame()
    {
        // Arrange
        var saga = new Variable(typeof(object), "saga");

        // Act
        var frame = _provider.CommitUnitOfWorkFrame(saga, _container);

        // Assert
        frame.ShouldBeOfType<AeroDBSessionSaveChangesFrame>();
    }

    // ====================================================================
    // DetermineUpdateFrame
    // ====================================================================

    [Test]
    public void DetermineUpdateFrame_ReturnsSagaOperationFrame()
    {
        // Arrange
        var saga = new Variable(typeof(object), "saga");

        // Act
        var frame = _provider.DetermineUpdateFrame(saga, _container);

        // Assert
        frame.ShouldBeOfType<SagaOperationFrame>();
    }

    // ====================================================================
    // DetermineDeleteFrame (saga overload)
    // ====================================================================

    [Test]
    public void DetermineDeleteFrame_WithSagaIdAndSaga_ReturnsSagaOperationFrame()
    {
        // Arrange
        var sagaId = new Variable(typeof(Guid), "sagaId");
        var saga = new Variable(typeof(object), "saga");

        // Act
        var frame = _provider.DetermineDeleteFrame(sagaId, saga, _container);

        // Assert
        frame.ShouldBeOfType<SagaOperationFrame>();
    }

    // ====================================================================
    // DetermineDeleteFrame (variable overload)
    // ====================================================================

    [Test]
    public void DetermineDeleteFrame_WithVariable_ReturnsSagaOperationFrame()
    {
        // Arrange
        var variable = new Variable(typeof(object), "entity");

        // Act
        var frame = _provider.DetermineDeleteFrame(variable, _container);

        // Assert
        frame.ShouldBeOfType<SagaOperationFrame>();
    }

    // ====================================================================
    // DetermineStoreFrame
    // ====================================================================

    [Test]
    public void DetermineStoreFrame_ReturnsSagaOperationFrame()
    {
        // Arrange
        var saga = new Variable(typeof(object), "saga");

        // Act
        var frame = _provider.DetermineStoreFrame(saga, _container);

        // Assert
        frame.ShouldBeOfType<SagaOperationFrame>();
    }

    // ====================================================================
    // DetermineStorageActionFrame — NotSupported
    // ====================================================================

    [Test]
    public void DetermineStorageActionFrame_ThrowsNotSupportedException()
    {
        // Arrange
        var action = new Variable(typeof(object), "action");

        // Act & Assert
        Should.Throw<NotSupportedException>(() =>
            _provider.DetermineStorageActionFrame(typeof(object), action, _container));
    }

    // ====================================================================
    // DetermineFrameToNullOutMaybeSoftDeleted — returns empty array
    // ====================================================================

    [Test]
    public void DetermineFrameToNullOutMaybeSoftDeleted_ReturnsEmptyArray()
    {
        // Arrange
        var entity = new Variable(typeof(object), "entity");

        // Act
        var frames = _provider.DetermineFrameToNullOutMaybeSoftDeleted(entity);

        // Assert
        frames.ShouldBeEmpty();
    }

    // ====================================================================
    // TryBuildFetchSpecificationFrame — returns false
    // ====================================================================

    [Test]
    public void TryBuildFetchSpecificationFrame_ReturnsFalse()
    {
        // Arrange
        var spec = new Variable(typeof(object), "spec");

        // Act
        var result = _provider.TryBuildFetchSpecificationFrame(spec, _container, out var frame, out var resultVar);

        // Assert
        result.ShouldBeFalse();
        frame.ShouldBeNull();
        resultVar.ShouldBeNull();
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    /// <summary>
    /// Create a minimal SagaChain for testing purposes.
    /// Uses a simple handler method with a string message parameter.
    /// </summary>
    private static Wolverine.Persistence.Sagas.SagaChain CreateMinimalSagaChain()
    {
        var handlerCall = new HandlerCall(typeof(SagaTestHandler), nameof(SagaTestHandler.Handle));
        return new Wolverine.Persistence.Sagas.SagaChain(
            handlerCall,
            new HandlerGraph(),
            System.Array.Empty<Endpoint>());
    }

    /// <summary>
    /// Minimal handler used to bootstrap SagaChain for tests.
    /// Must be public — HandlerCall uses reflection to find the method.
    /// </summary>
    public class SagaTestHandler
    {
        // ReSharper disable once UnusedMember.Global
        // ReSharper disable once UnusedParameter.Global
        public void Handle(string message)
        {
            // No-op: only needed to satisfy HandlerCall construction
        }
    }
}
