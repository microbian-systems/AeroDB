using AeroDB;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Dali.WolverineFx;

/// <summary>Side effect that executes against a AeroDB document session.</summary>
public interface IDaliOp : ISideEffect
{
    Task ExecuteAsync(IDocumentSession session, CancellationToken ct);
}

public static class DaliOps
{
    public static IDaliOp Store<T>(T entity) where T : class
        => new StoreOp<T>(entity);

    public static IDaliOp Delete<T>(T entity) where T : class
        => new DeleteOp<T>(entity);

    public static IDaliOp Insert<T>(T entity) where T : class
        => new InsertOp<T>(entity);
}

internal sealed class StoreOp<T> : IDaliOp where T : class
{
    private readonly T _entity;
    public StoreOp(T entity) => _entity = entity;
    public Task ExecuteAsync(IDocumentSession session, CancellationToken ct)
    {
        session.Store(_entity);
        return Task.CompletedTask;
    }
}

internal sealed class DeleteOp<T> : IDaliOp where T : class
{
    private readonly T _entity;
    public DeleteOp(T entity) => _entity = entity;
    public Task ExecuteAsync(IDocumentSession session, CancellationToken ct)
    {
        session.Delete(_entity);
        return Task.CompletedTask;
    }
}

internal sealed class InsertOp<T> : IDaliOp where T : class
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
/// Chain policy that detects <see cref="IDaliOp"/> and
/// <see cref="IEnumerable{T}"/> of <see cref="IDaliOp"/> return values in
/// handler chains and ensures AeroDB transaction support (session open, save,
/// flush) is applied. For collection returns, generates a foreach loop that
/// calls <see cref="IDaliOp.ExecuteAsync"/> on each item.
/// </summary>
internal sealed class DaliOpPolicy : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            var collections = chain.ReturnVariablesOfType<IEnumerable<IDaliOp>>().ToArray();
            var singles = chain.ReturnVariablesOfType<IDaliOp>().ToArray();

            if (collections.Any() || singles.Any())
            {
                new DaliPersistenceFrameProvider().ApplyTransactionSupport(chain, container);
            }

            foreach (var collection in collections)
            {
                collection.UseReturnAction(v => new ForEachDaliOpFrame(v));
            }
        }
    }
}

/// <summary>
/// Codegen frame that iterates a collection of <see cref="IDaliOp"/> values
/// and calls <see cref="IDaliOp.ExecuteAsync"/> on each one with the current
/// document session.
/// </summary>
internal sealed class ForEachDaliOpFrame : Frame
{
    private readonly Variable _collection;
    private Variable _session = null!;
    private Variable _cancellation = null!;

    public ForEachDaliOpFrame(Variable collection) : base(true)
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
            $"foreach (var item_of_{_collection.Usage} in {_collection.Usage}) await item_of_{_collection.Usage}.{nameof(IDaliOp.ExecuteAsync)}({_session.Usage}, {_cancellation.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Apply each AeroDB op to the current document session");
        writer.Write(
            $"for item_of_{_collection.Usage} in {_collection.Usage} do item_of_{_collection.Usage}.{nameof(IDaliOp.ExecuteAsync)}({_session.Usage}, {_cancellation.Usage})");
        Next?.GenerateFSharpCode(method, writer);
    }
}
