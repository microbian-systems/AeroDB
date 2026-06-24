using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Dali;
using Wolverine.Persistence;

namespace Dali.WolverineFx.Codegen;

/// <summary>
/// Dali-backed <see cref="IAncillaryStoreFrameProvider"/> that teaches the generic
/// <see cref="StorageAttribute"/> how to route a handler chain to a Dali ancillary store.
/// </summary>
internal sealed class DaliAncillaryStoreFrameProvider : IAncillaryStoreFrameProvider
{
    public bool Matches(Type storeType)
    {
        return storeType == typeof(IDocumentStore)
            || storeType.FullName == typeof(IDocumentStore).FullName;
    }

    public Frame BuildOutboxFactoryFrame(Type storeType)
    {
        return new OpenAncillaryDaliSessionFrame(storeType);
    }
}

/// <summary>
/// Frame that resolves a Dali ancillary store's outbox-enrolled session factory.
/// </summary>
internal sealed class OpenAncillaryDaliSessionFrame : Frame
{
    private readonly Type _storeType;
    private Variable? _ancillaryStoreFactory;

    public OpenAncillaryDaliSessionFrame(Type storeType) : base(false)
    {
        _storeType = storeType;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _ancillaryStoreFactory = chain.FindVariable(typeof(DaliOutboxedSessionFactory));
        yield return _ancillaryStoreFactory;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        Next?.GenerateCode(method, writer);
    }
}
