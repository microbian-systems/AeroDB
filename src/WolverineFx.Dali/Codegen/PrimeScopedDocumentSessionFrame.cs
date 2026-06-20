using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using Dali;
using Microsoft.Extensions.DependencyInjection;

namespace WolverineFx.Dali.Codegen;

/// <summary>
/// Emitted immediately after a handler's service-location child scope is created.
/// Primes that scope's <see cref="ScopedDocumentSessionHolder"/> with the handler's
/// outbox-enrolled <see cref="IDocumentSession"/>, so any service-located
/// <see cref="IDocumentSession"/> / <see cref="IQuerySession"/> resolves to that
/// single enrolled session. See GH-3001.
/// </summary>
internal sealed class PrimeScopedDocumentSessionFrame : SyncFrame, IUsesServiceProviderFrame
{
    private Variable? _session;
    private Variable? _scopedProvider;

    public void UseServiceProvider(Variable serviceProvider) => _scopedProvider = serviceProvider;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.TryFindVariable(typeof(IDocumentSession), VariableSource.NotServices);
        if (_session != null)
        {
            yield return _session;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_session != null)
        {
            writer.Write(
                $"{typeof(ServiceProviderServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{typeof(ScopedDocumentSessionHolder).FullNameInCode()}>({_scopedProvider!.Usage}).{nameof(ScopedDocumentSessionHolder.Session)} = {_session.Usage};");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_session != null)
        {
            writer.Write(
                $"{typeof(ServiceProviderServiceExtensions).FSharpName()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{typeof(ScopedDocumentSessionHolder).FSharpName()}>({_scopedProvider!.Usage}).{nameof(ScopedDocumentSessionHolder.Session)} <- {_session.Usage}");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
