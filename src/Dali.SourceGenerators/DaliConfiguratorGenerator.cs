using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dali.SourceGenerators;

[Generator]
public class DaliConfiguratorGenerator : IIncrementalGenerator
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
            var configureDaliType = compilation.GetTypeByMetadataName("Dali.IConfigureDali");
            var globalConfigureDaliType = compilation.GetTypeByMetadataName("Dali.IGlobalConfigureDali");
            var asyncConfigureDaliType = compilation.GetTypeByMetadataName("Dali.IAsyncConfigureDali");

            if (configureDaliType is null && asyncConfigureDaliType is null)
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

                // Check if this type implements IGlobalConfigureDali (applies to all stores).
                // Must be checked before IConfigureDali since IGlobalConfigureDali inherits from it.
                bool isGlobal = globalConfigureDaliType is not null &&
                    type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, globalConfigureDaliType));

                // Check if this type implements IConfigureDali (primary store only).
                // Exclude types that already registered as global — they get IGlobalConfigureDali registration instead.
                bool isConfigureDali = !isGlobal &&
                    configureDaliType is not null &&
                    type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, configureDaliType));

                if (isConfigureDali)
                {
                    syncConfigurators.Add(type);
                }

                if (isGlobal)
                {
                    globalConfigurators.Add(type);
                }

                // Check for IConfigureDali<TStore> (generic typed variant).
                // Only add if not already registered as a plain IConfigureDali or IGlobalConfigureDali implementor.
                if (!isGlobal && !isConfigureDali && configureDaliType is not null)
                {
                    var typedConfigureDali = compilation.GetTypeByMetadataName("Dali.IConfigureDali`1");
                    if (typedConfigureDali is not null)
                    {
                        foreach (var iface in type.AllInterfaces)
                        {
                            if (iface.IsGenericType)
                            {
                                var genericDef = iface.OriginalDefinition;
                                if (SymbolEqualityComparer.Default.Equals(genericDef, typedConfigureDali))
                                {
                                    syncConfigurators.Add(type);
                                    break;
                                }
                            }
                        }
                    }
                }

                // Check if this type implements IAsyncConfigureDali
                if (asyncConfigureDaliType is not null &&
                    type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, asyncConfigureDaliType)))
                {
                    asyncConfigurators.Add(type);
                }
            }

            var sourceText = GenerateRegistrar(syncConfigurators, globalConfigurators, asyncConfigurators);
            ctx.AddSource("DaliConfiguratorRegistrar.g.cs", sourceText);
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
    /// Generates the <c>DaliConfiguratorRegistrar</c> class with an extension method
    /// that registers all discovered <see cref="global::Dali.IConfigureDali"/>,
    /// <see cref="global::Dali.IGlobalConfigureDali"/>, and
    /// <see cref="global::Dali.IAsyncConfigureDali"/> implementations.
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
        sb.AppendLine("namespace Dali.Generated;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Source-generated registration helper for discovered IConfigureDali / IGlobalConfigureDali / IAsyncConfigureDali implementations.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class DaliConfiguratorRegistrar");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Registers all discovered IConfigureDali, IGlobalConfigureDali, and IAsyncConfigureDali implementations as singletons.");
        sb.AppendLine("    /// Call before AddDali() to enable DI auto-discovery.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static IServiceCollection AddDiscoveredDaliConfigurators(this IServiceCollection services)");
        sb.AppendLine("    {");

        // Sync configurators (primary store only)
        foreach (var type in syncConfigurators)
        {
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"        services.AddSingleton<global::Dali.IConfigureDali, {fqn}>();");
        }

        // Global configurators (all store types)
        foreach (var type in globalConfigurators)
        {
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"        services.AddSingleton<global::Dali.IGlobalConfigureDali, {fqn}>();");
        }

        // Async configurators
        foreach (var type in asyncConfigurators)
        {
            var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"        services.AddSingleton<global::Dali.IAsyncConfigureDali, {fqn}>();");
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
