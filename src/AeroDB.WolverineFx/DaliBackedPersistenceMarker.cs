using JasperFx.CodeGeneration.Model;

namespace AeroDB.WolverineFx;

/// <summary>
/// Marker type used by Wolverine's code generation to resolve
/// <see cref="DaliMessageStore"/> as the backing persistence strategy.
/// Mirrors <c>DatabaseBackedPersistenceMarker</c> from Wolverine.RDBMS.
/// </summary>
internal sealed class DaliBackedPersistenceMarker : IVariableSource
{
    public bool Matches(Type type) => type == GetType();

    public Variable Create(Type type) => Variable.For<DaliMessageStore>();
}
