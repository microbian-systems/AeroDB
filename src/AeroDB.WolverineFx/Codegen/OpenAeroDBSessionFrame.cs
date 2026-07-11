using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using AeroDB.Sable;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace AeroDB.WolverineFx.Codegen;

/// <summary>
/// Codegen frame that opens an outbox-enrolled <see cref="IDocumentSession"/>
/// via <see cref="AeroDBOutboxedSessionFactory"/>. Inserted as middleware in handler
/// chains that need AeroDB.Sable persistence.
/// </summary>
internal sealed class OpenAeroDBSessionFrame : Frame
{
    private readonly IChain _chain;
    private Variable? _cancellation;
    private Variable? _context;
    private bool _createsSession;
    private Variable? _factory;

    public OpenAeroDBSessionFrame(IChain chain) : base(true)
    {
        _chain = chain;
    }

    public Variable? Session { get; private set; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        Session = chain.TryFindVariable(typeof(IDocumentSession), VariableSource.NotServices);
        if (Session == null)
        {
            _createsSession = true;
            Session = new Variable(typeof(IDocumentSession), this);

            _factory = chain.FindVariable(typeof(AeroDBOutboxedSessionFactory));
            yield return _factory;
        }

        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices);
        if (_context != null)
        {
            yield return _context;
        }

        if (Session != null)
        {
            yield return Session;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_createsSession)
        {
            writer.BlankLine();
            writer.WriteComment("Open a new AeroDB.Sable document session registered with the Wolverine");
            writer.WriteComment("message context to support the outbox functionality");
            writer.Write(
                $"using var {Session!.Usage} = await {_factory!.Usage}.{nameof(AeroDBOutboxedSessionFactory.OpenSession)}({_context!.Usage}).ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_createsSession)
        {
            writer.BlankLine();
            writer.WriteComment("Open a new AeroDB.Sable document session registered with the Wolverine");
            writer.WriteComment("message context to support the outbox functionality");
            writer.Write(
                $"use {Session!.Usage} = {_factory!.Usage}.{nameof(AeroDBOutboxedSessionFactory.OpenSession)}({_context!.Usage})");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
