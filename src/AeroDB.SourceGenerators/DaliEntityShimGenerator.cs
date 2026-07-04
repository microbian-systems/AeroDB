using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Linq;

namespace AeroDB.SourceGenerators;

/// <summary>
/// Generates CBOR-compatible shim <c>Record</c> types for <c>Entity&lt;TId&gt;</c> subclasses.
/// Enables <c>LoadAsync&lt;T&gt;()</c> via the <c>DeserializeViaShimAsync&lt;T&gt;()</c> path:
/// the shim is deserialized by the SurrealDB SDK (CBOR), then materialized to the entity via <c>ToEntity()</c>.
/// </summary>
[Generator]
public class DaliEntityShimGenerator : IIncrementalGenerator
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

        // Step 3: Register output for per-type shim files
        context.RegisterSourceOutput(compilationAndTypes, static (ctx, source) =>
        {
            var (compilation, types) = source;

            // Find the Entity<TId> type in AeroDB
            var entityGenericType = compilation.GetTypeByMetadataName("AeroDB.Entity`1");
            if (entityGenericType is null) return;

            // Find the Record type in SurrealDb.Net (needed for shim base class)
            var recordType = compilation.GetTypeByMetadataName("SurrealDb.Net.Models.Record");
            if (recordType is null) return;

            // Optionally find the DaliDocumentAttribute for opt-out
            var daliDocAttrType = compilation.GetTypeByMetadataName("AeroDB.DaliDocumentAttribute");

            foreach (var type in types)
            {
                if (type is null) continue;
                if (type.IsAbstract) continue;
                if (!IsEntitySubclass(type, entityGenericType)) continue;

                // Check for [DaliDocument(SkipGeneration = true)] opt-out
                if (daliDocAttrType is not null)
                {
                    var skipAttr = type.GetAttributes()
                        .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, daliDocAttrType));
                    if (skipAttr is not null)
                    {
                        var skipNamedArg = skipAttr.NamedArguments
                            .FirstOrDefault(kv => kv.Key == "SkipGeneration");
                        if (skipNamedArg.Key == "SkipGeneration" && skipNamedArg.Value.Value is true)
                            continue;
                    }
                }

                var idType = GetEntityIdType(type, entityGenericType);
                var sourceText = GenerateShimClass(type, idType);
                var hintName = $"{type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)).Replace("global::", "").Replace(".", "_")}.Shim.g.cs";
                ctx.AddSource(hintName, sourceText);
            }
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
    /// Checks if <paramref name="type"/> is a subclass of <c>Entity&lt;TId&gt;</c>.
    /// </summary>
    private static bool IsEntitySubclass(INamedTypeSymbol type, INamedTypeSymbol entityGenericType)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(current.ConstructedFrom, entityGenericType))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Extracts the <c>TId</c> type argument from <c>Entity&lt;TId&gt;</c> in the inheritance chain.
    /// </summary>
    private static ITypeSymbol? GetEntityIdType(INamedTypeSymbol type, INamedTypeSymbol entityGenericType)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(current.ConstructedFrom, entityGenericType))
                return current.TypeArguments[0];
            current = current.BaseType;
        }
        return null;
    }

    /// <summary>
    /// Generates the shim class source for the given entity type.
    /// The shim extends <c>Record</c> and mirrors writable properties with <c>CborPropertyAttribute</c>.
    /// </summary>
    private static string GenerateShimClass(INamedTypeSymbol type, ITypeSymbol? idType)
    {
        var typeName = type.Name;
        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var ns = type.ContainingNamespace.ToDisplayString();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#pragma warning disable CS8669, CS8618");
        sb.AppendLine();
        sb.AppendLine("using global::SurrealDb.Net.Models;");
        sb.AppendLine("using global::Dahomey.Cbor.Attributes;");
        sb.AppendLine();
        sb.AppendLine("namespace AeroDB.Metadata;");
        sb.AppendLine();
        sb.AppendLine($"internal sealed class {typeName}Shim : Record");
        sb.AppendLine("{");
        sb.AppendLine($"    static {typeName}Shim()");
        sb.AppendLine("    {");
        sb.AppendLine($"        global::AeroDB.Metadata.MetadataRegistry.RegisterShimType<{fullName}>(typeof({typeName}Shim));");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Gather public read/write properties (excluding Id — handled by Record base)
        var properties = new List<string>();
        foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.Name == "Id") continue;
            if (member.DeclaredAccessibility != Accessibility.Public) continue;
            if (member.IsStatic) continue;
            if (member.GetMethod is null || member.SetMethod is null) continue;

            var cborName = char.ToLowerInvariant(member.Name[0]) + member.Name.Substring(1);
            var typeStr = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            sb.AppendLine($"    [CborProperty(\"{cborName}\")]");
            sb.AppendLine($"    public {typeStr} {member.Name} {{ get; set; }}");
            sb.AppendLine();
            properties.Add(member.Name);
        }

        // ToEntity() method
        sb.AppendLine($"    public {fullName} ToEntity()");
        sb.AppendLine("    {");
        sb.AppendLine($"        var entity = new {fullName}();");

        // ID extraction from RecordId based on TId type
        if (idType is not null)
        {
            var idTypeName = idType.ToDisplayString();

            if (idTypeName is "long" or "System.Int64")
            {
                sb.AppendLine("        if (this.Id is RecordIdOf<long> lr)");
                sb.AppendLine("            entity.Id = lr.Id;");
                sb.AppendLine("        else if (this.Id is RecordIdOf<int> ir)");
                sb.AppendLine("            entity.Id = ir.Id;");
                sb.AppendLine("        else if (this.Id is RecordIdOf<string> sr && long.TryParse(sr.Id, out var lid))");
                sb.AppendLine("            entity.Id = lid;");
                sb.AppendLine("        else if (this.Id is not null)");
                sb.AppendLine("            entity.Id = long.Parse(this.Id.ToString()!);");
            }
            else if (idTypeName is "string" or "System.String")
            {
                sb.AppendLine("        if (this.Id is RecordIdOf<string> sr)");
                sb.AppendLine("            entity.Id = sr.Id;");
                sb.AppendLine("        else if (this.Id is not null)");
                sb.AppendLine("            entity.Id = this.Id.ToString()!;");
                sb.AppendLine("        else");
                sb.AppendLine("            entity.Id = string.Empty;");
            }
            else if (idTypeName is "int" or "System.Int32")
            {
                sb.AppendLine("        if (this.Id is RecordIdOf<int> ir)");
                sb.AppendLine("            entity.Id = ir.Id;");
                sb.AppendLine("        else if (this.Id is RecordIdOf<long> lr)");
                sb.AppendLine("            entity.Id = (int)lr.Id;");
                sb.AppendLine("        else if (this.Id is RecordIdOf<string> sr && int.TryParse(sr.Id, out var iid))");
                sb.AppendLine("            entity.Id = iid;");
                sb.AppendLine("        else if (this.Id is not null)");
                sb.AppendLine("            entity.Id = int.Parse(this.Id.ToString()!);");
            }
            else if (idTypeName is "System.Guid")
            {
                sb.AppendLine("        if (this.Id is RecordIdOf<string> sr)");
                sb.AppendLine("            entity.Id = Guid.Parse(sr.Id);");
                sb.AppendLine("        else if (this.Id is not null)");
                sb.AppendLine("            entity.Id = Guid.Parse(this.Id.ToString()!);");
                sb.AppendLine("        else");
                sb.AppendLine("            entity.Id = Guid.Empty;");
            }
            else
            {
                sb.AppendLine("        entity.Id = default;");
            }
        }
        else
        {
            sb.AppendLine("        entity.Id = default;");
        }

        // Copy remaining properties
        foreach (var prop in properties)
        {
            sb.AppendLine($"        entity.{prop} = this.{prop};");
        }

        sb.AppendLine("        return entity;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
