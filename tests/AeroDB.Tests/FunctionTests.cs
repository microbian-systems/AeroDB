using System.Reflection;
using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

public class FunctionTests
{
    [Test]
    public async Task SurrealFunction_Defaults()
    {
        var fn = new SurrealFunction();

        fn.Name.ShouldBeEmpty();
        fn.Body.ShouldBeEmpty();
        fn.Parameters.ShouldBeNull();
        fn.ParametersTyped.ShouldNotBeNull();
        fn.ParametersTyped.Count.ShouldBe(0);
    }

    [Test]
    public async Task SurrealFunction_CanSetProperties()
    {
        var fn = new SurrealFunction
        {
            Name = "fn::greet",
            Body = "RETURN 'Hello, ' + $name;",
            Parameters = "$name: string"
        };

        fn.Name.ShouldBe("fn::greet");
        fn.Body.ShouldBe("RETURN 'Hello, ' + $name;");
        fn.Parameters.ShouldBe("$name: string");
    }

    [Test]
    public async Task SurrealFunctionParameter_Creation()
    {
        var param = new SurrealFunctionParameter
        {
            Name = "name",
            Type = "string",
            IsOptional = false
        };

        param.Name.ShouldBe("name");
        param.Type.ShouldBe("string");
        param.IsOptional.ShouldBeFalse();
    }

    [Test]
    public async Task SurrealFunction_WithTypedParameters()
    {
        var fn = new SurrealFunction
        {
            Name = "fn::add",
            Body = "RETURN $a + $b;",
            ParametersTyped =
            {
                new() { Name = "a", Type = "int" },
                new() { Name = "b", Type = "int" }
            }
        };

        fn.ParametersTyped.Count.ShouldBe(2);
        fn.ParametersTyped[0].Name.ShouldBe("a");
        fn.ParametersTyped[0].Type.ShouldBe("int");
        fn.ParametersTyped[1].Name.ShouldBe("b");
        fn.ParametersTyped[1].Type.ShouldBe("int");
    }

    [Test]
    public async Task FunctionOptions_Register_StringParams()
    {
        var options = new FunctionOptions();
        options.Register("fn::greet", "RETURN 'Hello, ' + $name;", "$name: string");

        options.Functions.Count.ShouldBe(1);
        options.Functions[0].Name.ShouldBe("fn::greet");
        options.Functions[0].Body.ShouldBe("RETURN 'Hello, ' + $name;");
        options.Functions[0].Parameters.ShouldBe("$name: string");
    }

    [Test]
    public async Task FunctionOptions_Register_TypedParams()
    {
        var options = new FunctionOptions();
        options.Register("fn::add", "RETURN $a + $b;",
            new SurrealFunctionParameter { Name = "a", Type = "int" },
            new SurrealFunctionParameter { Name = "b", Type = "int" });

        options.Functions.Count.ShouldBe(1);
        options.Functions[0].Name.ShouldBe("fn::add");
        options.Functions[0].ParametersTyped.Count.ShouldBe(2);
        options.Functions[0].ParametersTyped[0].Name.ShouldBe("a");
        options.Functions[0].ParametersTyped[0].Type.ShouldBe("int");
    }

    [Test]
    public async Task FunctionOptions_Register_NoParams()
    {
        var options = new FunctionOptions();
        options.Register("fn::pi", "RETURN 3.14159;");

        options.Functions.Count.ShouldBe(1);
        options.Functions[0].Name.ShouldBe("fn::pi");
        options.Functions[0].Body.ShouldBe("RETURN 3.14159;");
        options.Functions[0].Parameters.ShouldBeNull();
        options.Functions[0].ParametersTyped.Count.ShouldBe(0);
    }

    [Test]
    public async Task FunctionOptions_AutoCreateDefault()
    {
        var options = new FunctionOptions();
        options.AutoCreateFunctions.ShouldBeTrue();
    }

    [Test]
    public async Task FunctionOptions_CanSetAutoCreateFalse()
    {
        var options = new FunctionOptions
        {
            AutoCreateFunctions = false
        };
        options.AutoCreateFunctions.ShouldBeFalse();
    }

    [Test]
    public async Task FunctionOptions_ChainedCalls()
    {
        var options = new FunctionOptions();
        options.Register("fn::a", "RETURN 1;")
               .Register("fn::b", "RETURN 2;");

        options.Functions.Count.ShouldBe(2);
        options.Functions[0].Name.ShouldBe("fn::a");
        options.Functions[1].Name.ShouldBe("fn::b");
    }

    [Test]
    public async Task FunctionManager_BuildsCorrectSurql_StringParams()
    {
        var fn = new SurrealFunction
        {
            Name = "fn::greet",
            Body = "RETURN 'Hello, ' + $name;",
            Parameters = "$name: string"
        };

        var method = typeof(FunctionManager).GetMethod("BuildDefineFunctionSurql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var surql = (string)method!.Invoke(null, [fn])!;

        surql.ShouldBe("DEFINE FUNCTION fn::greet($name: string) { RETURN 'Hello, ' + $name; };");
    }

    [Test]
    public async Task FunctionManager_BuildsCorrectSurql_TypedParams()
    {
        var fn = new SurrealFunction
        {
            Name = "fn::add",
            Body = "RETURN $a + $b;",
            ParametersTyped =
            {
                new() { Name = "a", Type = "int" },
                new() { Name = "b", Type = "int" }
            }
        };

        var method = typeof(FunctionManager).GetMethod("BuildDefineFunctionSurql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var surql = (string)method!.Invoke(null, [fn])!;

        surql.ShouldContain("DEFINE FUNCTION fn::add");
        surql.ShouldContain("$a: int");
        surql.ShouldContain("$b: int");
        surql.ShouldContain("RETURN $a + $b;");
    }

    [Test]
    public async Task FunctionManager_BuildsNoParams()
    {
        var fn = new SurrealFunction
        {
            Name = "fn::pi",
            Body = "RETURN 3.14159;"
        };

        var method = typeof(FunctionManager).GetMethod("BuildDefineFunctionSurql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var surql = (string)method!.Invoke(null, [fn])!;

        surql.ShouldContain("DEFINE FUNCTION fn::pi()");
        surql.ShouldContain("RETURN 3.14159;");
    }

    [Test]
    public async Task FunctionManager_BuildsSurql_WithOptionalParams()
    {
        var fn = new SurrealFunction
        {
            Name = "fn::search",
            Body = "RETURN $query;",
            ParametersTyped =
            {
                new() { Name = "query", Type = "string" },
                new() { Name = "limit", Type = "int", IsOptional = true }
            }
        };

        var method = typeof(FunctionManager).GetMethod("BuildDefineFunctionSurql",
            BindingFlags.NonPublic | BindingFlags.Static);
        var surql = (string)method!.Invoke(null, [fn])!;

        surql.ShouldContain("DEFINE FUNCTION fn::search");
        surql.ShouldContain("$query: string");
        surql.ShouldContain("$limit: int");
        surql.ShouldContain("RETURN $query;");
    }

    [Test]
    public async Task SurrealFunctionParameter_IsOptional()
    {
        var param = new SurrealFunctionParameter
        {
            Name = "limit",
            Type = "int",
            IsOptional = true
        };

        param.IsOptional.ShouldBeTrue();
        param.Name.ShouldBe("limit");
        param.Type.ShouldBe("int");
    }

    [Test]
    public async Task FunctionOptions_StoredInStoreOptions()
    {
        var options = new StoreOptions();
        options.Functions.Register("fn::custom", "RETURN 42;");

        options.Functions.Functions.Count.ShouldBe(1);
        options.Functions.Functions[0].Name.ShouldBe("fn::custom");
    }

    [Test]
    public async Task SurrealFunctionParameter_DefaultType()
    {
        var param = new SurrealFunctionParameter
        {
            Name = "x"
        };

        param.Name.ShouldBe("x");
        param.Type.ShouldBe("string"); // default
        param.IsOptional.ShouldBeFalse();
    }
}
