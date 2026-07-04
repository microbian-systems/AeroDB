using JasperFx.CodeGeneration.Model;

namespace AeroDB.WolverineFx.Codegen;

/// <summary>
/// Codegen variable source that makes <see cref="AeroDBOutboxedSessionFactory"/>
/// available for handler chains.
/// </summary>
internal sealed class AeroDBOutboxedSessionFactorySource : IVariableSource
{
    public bool Matches(Type type) => type == typeof(AeroDBOutboxedSessionFactory);

    public Variable Create(Type type) => Variable.For<AeroDBOutboxedSessionFactory>();
}
