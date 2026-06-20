using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Dali;

namespace WolverineFx.Dali.Codegen;

/// <summary>
/// Codegen frame that loads a saga document using <see cref="IDocumentSession.LoadAsync{T}"/>
/// from the Dali session.
/// </summary>
internal sealed class LoadSagaFrame : AsyncFrame
{
    private readonly Variable _sagaId;
    private readonly Type _sagaType;
    private Variable? _cancellation;
    private Variable? _session;

    public LoadSagaFrame(Type sagaType, Variable sagaId)
    {
        _sagaType = sagaType;
        _sagaId = sagaId;
        uses.Add(sagaId);

        var usage = $"{Variable.DefaultArgName(sagaType)}_{sagaId.Usage.Split('.').Last()}";
        Saga = new Variable(sagaType, usage, this);
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _sagaId;

        _session = chain.FindVariable(typeof(IDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Try to load the existing saga document from Dali");
        writer.Write(
            $"var {Saga.Usage} = await {_session!.Usage}.{nameof(IDocumentSession.LoadAsync)}<{_sagaType.FullName}>({_sagaId.Usage}.ToString(), {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}
