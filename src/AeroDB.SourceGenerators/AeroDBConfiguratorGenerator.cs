using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AeroDB.SourceGenerators;

[Generator]
public class AeroDBConfiguratorGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Step 1: Find all class declarations
        var typeDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => GetNamedTypeSymbol(ctx))
            .Where(static symbol => symbol is not null);

        // Step 2: Combine with compilation to access type metadata
        var compilationAndTypes = context.CompilationProvider.Combine(typeDeclarations.Collect());

        // Step 3: Register output for the combined configurator registrar
        context.RegisterSourceOutput(compilationAndTypes, static (ctx, source) =>
        {
            var (compilation, types) = source;

            // Look for the configurator interfaces in the compilation.
            // If neither exists, there's nothing to generate.
            var configureAeroDBType = compilation.GetTypeByMetadataName("AeroDB.Sable.IConfigureAeroDB");
            var globalConfigureAeroDBType = compilation.GetTypeByMetadataName("AeroDB.Sable.IGlobalConfigureAeroDB");
            var asyncConfigureAeroDBType = compilation.GetTypeByMetadataName("AeroDB.Sable.IAsyncConfigureAeroDB");

            if (configureAeroDBType is null && asyncConfigureAeroDBType is null)
                return;

            var syncConfigurators = new List<INamedTypeSymbol>();
            var globalConfigurators = new List<INamedTypeSymbol>();
            var asyncConfigurators = new List<INamedTypeSymbol>();

            foreach (var type in types)
            {
                if (type is null) continue;

                // Skip abstract classes
                if (type.IsAbstract) continue;

                // Skip generic types
                if (type.TypeParameters.Length > 0) continue;

                // Skip static classes (they can't implement interfaces meaningfully)
                if (type.IsStatic) continue;

                // Skip types not accessible from the consumer's assembly
                if (type.DeclaredAccessibility != Accessibility.Public &&
                    type.DeclaredAccessibility != Accessibility.Internal)
                    continue;

                // Check if this type implements IGlobalConfigureAeroDB (applies to all stores).
                // Must be checked before IConfigureAeroDB since IGlobalConfigureAeroDB inherits from it.
                bool isGlobal = globalConfigureAeroDBType is not null &&
                    type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, globalConfigureAeroDBType));

                // Check if this type implements IConfigureAeroDB (primary store only).
                // Exclude types that already registered as global — they get IGlobalConfigureAeroDB registration instead.
                bool isConfigureAeroDB = !isGlobal &&
                    configureAeroDBType is not null &&
                    type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, configureAeroDBType));

                if (isConfigureAeroDB)
                {
                    syncConfigurators.Add(type);
                }

                if (isGlobal)
                {
                    globalConfigurators.Add(type);
                }

                // Check for IConfigureAeroDB<TStore> (generic typed variant).
                // Only add if not already registered as a plain IConfigureAeroDB or IGlobalConfigureAeroDB implementor.
                if (!isGlobal && !isConfigureAeroDB && configureAeroDBType is not null)
                {
                    var typedConfigureAeroDB = compilation.GetTypeByMetadataName("AeroDB.Sable.IConfigureAeroDB`1");
                    if (typedConfigureAeroDB is not null)
                    {
                        foreach (var iface in type.AllInterfaces)
                        {
                            if (iface.IsGenericType)
                            {
                                var genericDef = iface.OriginalDefinition;
                                if (SymbolEqualityComparer.Default.Equals(genericDef, typedConfigureAeroDB))
                                {
                                    syncConfigurators.Add(type);
                                    break;
                                }
                            }
                        }
                    }
                }

                // Check if this type implements IAsyncConfigureAeroDB
                if (asyncConfigureAeroDBType is not null &&
                    type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, asyncConfigureAeroDBType)))
                {
                    asyncConfigurators.Add(type);
                }
            }

            var sourceText = GenerateRegistrar(syncConfigurators, globalConfigurators, asyncConfigurators);
            ctx.AddSource("AeroDBConfiguratorRegistrar.g.cs", sourceText);
        });
    }

    private static INamedTypeSymbol? GetNamedTypeSymbol(GeneratorSyntaxContext ctx)
    {
        if (ctx.Node is ClassDeclarationSyntax classDecl)
        {
            var symbol = ctx.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            return symbol;
        }
        return null;
    }

    /// <summary>
    /// Generates the <c>AeroDBConfiguratorRegistrar</c> class with an extension method
    /// that registers all discovered <c>IConfigureAeroDB</c>,
    /// <c>IGlobalConfigureAeroDB</c>, and
    /// <c>IAsyncConfigureAeroDB</c> implementations.
    /// </summary>
    private static string GenerateRegistrar(
        List<INamedTypeSymbol> syncConfigurators,
        List<INamedTypeSymbol> globalConfigurators,
        List<INamedTypeSymbol> asyncConfigurators)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#pragma warning disable CS8669, CS8618");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        sb.AppendLine("namespace AeroDB.Sable.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Source-generated registration helper for discovered IConfigureAeroDB / IGlobalConfigureAeroDB / IAsyncConfigureAeroDB implementations.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class AeroDBConfiguratorRegistrar");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Registers all discovered IConfigureAeroDB, IGlobalConfigureAeroDB, and IAsyncConfigureAeroDB implementations as singletons.");
        sb.AppendLine("    /// Call before AddAeroDB() to enable DI auto-discovery.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static IServiceCollection AddDiscoveredAeroDBConfigurators(this IServiceCollection services)");
        sb.AppendLine("    {");

        // Sync configurators (primary store only)
        foreach (var type in syncConfigurators)
        {
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"        services.AddSingleton<global::AeroDB.Sable.IConfigureAeroDB, {fqn}>();");
        }

        // Global configurators (all store types)
        foreach (var type in globalConfigurators)
        {
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"        services.AddSingleton<global::AeroDB.Sable.IGlobalConfigureAeroDB, {fqn}>();");
        }

        // Async configurators
        foreach (var type in asyncConfigurators)
        {
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"        services.AddSingleton<global::AeroDB.Sable.IAsyncConfigureAeroDB, {fqn}>();");
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
