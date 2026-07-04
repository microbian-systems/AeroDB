using AeroDB;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace AeroDB.WolverineFx;

/// <summary>Side effect that executes against a AeroDB document session.</summary>
public interface IAeroDBOp : ISideEffect
{
    Task ExecuteAsync(IDocumentSession session, CancellationToken ct);
}

public static class AeroDBOps
{
    public static IAeroDBOp Store<T>(T entity) where T : class
        => new StoreOp<T>(entity);

    public static IAeroDBOp Delete<T>(T entity) where T : class
        => new DeleteOp<T>(entity);

    public static IAeroDBOp Insert<T>(T entity) where T : class
        => new InsertOp<T>(entity);
}

internal sealed class StoreOp<T> : IAeroDBOp where T : class
{
    private readonly T _entity;
    public StoreOp(T entity) => _entity = entity;
    public Task ExecuteAsync(IDocumentSession session, CancellationToken ct)
    {
        session.Store(_entity);
        return Task.CompletedTask;
    }
}

internal sealed class DeleteOp<T> : IAeroDBOp where T : class
{
    private readonly T _entity;
    public DeleteOp(T entity) => _entity = entity;
    public Task ExecuteAsync(IDocumentSession session, CancellationToken ct)
    {
        session.Delete(_entity);
        return Task.CompletedTask;
    }
}

internal sealed class InsertOp<T> : IAeroDBOp where T : class
{
    private readonly T _entity;
    public InsertOp(T entity) => _entity = entity;
    public Task ExecuteAsync(IDocumentSession session, CancellationToken ct)
    {
        session.Store(_entity);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Chain policy that detects <see cref="IAeroDBOp"/> and
/// <see cref="IEnumerable{T}"/> of <see cref="IAeroDBOp"/> return values in
/// handler chains and ensures AeroDB transaction support (session open, save,
/// flush) is applied. For collection returns, generates a foreach loop that
/// calls <see cref="IAeroDBOp.ExecuteAsync"/> on each item.
/// </summary>
internal sealed class AeroDBOpPolicy : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            var collections = chain.ReturnVariablesOfType<IEnumerable<IAeroDBOp>>().ToArray();
            var singles = chain.ReturnVariablesOfType<IAeroDBOp>().ToArray();

            if (collections.Any() || singles.Any())
            {
                new AeroDBPersistenceFrameProvider().ApplyTransactionSupport(chain, container);
            }

            foreach (var collection in collections)
            {
                collection.UseReturnAction(v => new ForEachAeroDBOpFrame(v));
            }
        }
    }
}

/// <summary>
/// Codegen frame that iterates a collection of <see cref="IAeroDBOp"/> values
/// and calls <see cref="IAeroDBOp.ExecuteAsync"/> on each one with the current
/// document session.
/// </summary>
internal sealed class ForEachAeroDBOpFrame : Frame
{
    private readonly Variable _collection;
    private Variable _session = null!;
    private Variable _cancellation = null!;

    public ForEachAeroDBOpFrame(Variable collection) : base(true)
    {
        _collection = collection;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        yield return _collection;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Apply each AeroDB op to the current document session");
        writer.Write(
            $"foreach (var item_of_{_collection.Usage} in {_collection.Usage}) await item_of_{_collection.Usage}.{nameof(IAeroDBOp.ExecuteAsync)}({_session.Usage}, {_cancellation.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Apply each AeroDB op to the current document session");
        writer.Write(
            $"for item_of_{_collection.Usage} in {_collection.Usage} do item_of_{_collection.Usage}.{nameof(IAeroDBOp.ExecuteAsync)}({_session.Usage}, {_cancellation.Usage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}
