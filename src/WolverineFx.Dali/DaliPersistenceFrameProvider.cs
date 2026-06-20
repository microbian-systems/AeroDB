using Dali;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using WolverineFx.Dali.Codegen;

namespace WolverineFx.Dali;

/// <summary>
/// Dali-backed <see cref="IPersistenceFrameProvider"/> that generates codegen frames
/// for saga persistence operations through Dali's IDocumentSession.
/// Mirrors the MartenPersistenceFrameProvider pattern.
/// </summary>
internal sealed class DaliPersistenceFrameProvider : IPersistenceFrameProvider
{
    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IDocumentSession);
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        return typeof(Guid);
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (!chain.Middleware.OfType<OpenDaliSessionFrame>().Any())
        {
            chain.Middleware.Add(new OpenDaliSessionFrame(chain));
        }

        if (chain is not SagaChain)
        {
            if (!chain.Postprocessors.OfType<DaliSessionSaveChangesFrame>().Any())
            {
                chain.Postprocessors.Add(new DaliSessionSaveChangesFrame());
            }

            if (!chain.Postprocessors.OfType<FlushDaliOutgoingMessagesFrame>().Any())
            {
                chain.Postprocessors.Add(new FlushDaliOutgoingMessagesFrame());
            }
        }
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        ApplyTransactionSupport(chain, container);
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        if (chain is SagaChain) return true;

        var serviceDependencies = chain
            .ServiceDependencies(container, new[] { typeof(IDocumentSession), typeof(IQuerySession) })
            .ToArray();
        return serviceDependencies.Any(x => x == typeof(IDocumentSession));
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        return new LoadSagaFrame(sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        return new SagaOperationFrame(saga, nameof(IDocumentSession.Store));
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        return new DaliSessionSaveChangesFrame();
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return new SagaOperationFrame(saga, nameof(IDocumentSession.Store));
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new SagaOperationFrame(saga, nameof(IDocumentSession.Delete));
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        return new SagaOperationFrame(saga, nameof(IDocumentSession.Store));
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return new SagaOperationFrame(variable, nameof(IDocumentSession.Delete));
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        throw new NotSupportedException("Storage action frames are not supported by Dali persistence.");
    }

    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)
    {
        return [];
    }

    public bool TryBuildFetchSpecificationFrame(
        Variable specVariable,
        IServiceContainer container,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Frame? frame,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Variable? result)
    {
        frame = null;
        result = null;
        return false;
    }
}
