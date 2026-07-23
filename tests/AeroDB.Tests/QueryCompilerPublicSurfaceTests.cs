using AeroDB.Sable;
using Shouldly;

namespace AeroDB.Tests;

public class QueryCompilerPublicSurfaceTests
{
    [Test]
    public void Compiler_implementation_types_are_not_public_contracts()
    {
        Type[] implementationTypes =
        [
            typeof(SurrealExpressionVisitor),
            typeof(SurrealQueryResult),
            typeof(SurrealQueryProvider),
            typeof(CompiledPlan),
            typeof(CompiledQueryPlanner),
            typeof(CompiledQueryProvider<>),
        ];

        implementationTypes.ShouldAllBe(type => type.IsNotPublic);
        typeof(SableCommand).IsPublic.ShouldBeTrue();
    }
}
