using JasperFx.CodeGeneration.Model;

namespace AeroDB.WolverineFx.Codegen;

/// <summary>
/// Codegen variable source that makes <see cref="DaliOutboxedSessionFactory"/>
/// available for handler chains.
/// </summary>
internal sealed class DaliOutboxedSessionFactorySource : IVariableSource
{
    public bool Matches(Type type) => type == typeof(DaliOutboxedSessionFactory);

    public Variable Create(Type type) => Variable.For<DaliOutboxedSessionFactory>();
}
