using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace Dali.SourceGenerators;

[Generator]
public class DaliDocumentGenerator : IIncrementalGenerator
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

        // Step 3: Register output for per-type metadata files
        context.RegisterSourceOutput(compilationAndTypes, static (ctx, source) =>
        {
            var (compilation, types) = source;

            // Find the Record type in SurrealDb.Net
            var recordType = compilation.GetTypeByMetadataName("SurrealDb.Net.Models.Record");
            if (recordType is null)
                return;

            // Optionally find the DaliDocumentAttribute — if it's not available
            // in the compilation (e.g., consumer doesn't reference the attribute assembly),
            // we skip the opt-out check and generate for all Record subclasses.
            var daliDocAttrType = compilation.GetTypeByMetadataName("Dali.DaliDocumentAttribute");

            var validTypes = new List<INamedTypeSymbol>();

            foreach (var type in types)
            {
                if (type is null) continue;
                if (!IsRecordSubclass(type, recordType)) continue;

                // Check for [DaliDocument(SkipGeneration = true)] — opt-out
                if (daliDocAttrType is not null)
                {
                    var skipAttr = type.GetAttributes()
                        .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, daliDocAttrType));
                    if (skipAttr is not null)
                    {
                        var skipNamedArg = skipAttr.NamedArguments
                            .FirstOrDefault(kv => kv.Key == "SkipGeneration");
                        if (skipNamedArg.Key == "SkipGeneration" &&
                            skipNamedArg.Value.Value is true)
                            continue;
                    }
                }

                validTypes.Add(type);
            }

            // Generate per-type metadata files
            foreach (var type in validTypes)
            {
                var sourceText = GenerateMetadataClass(type, compilation);
                var hintName = $"{type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)).Replace("global::", "").Replace(".", "_")}.Metadata.g.cs";
                ctx.AddSource(hintName, sourceText);
            }

            // NOTE: MetadataDispatch is a hand-written class in src/Dali/Metadata/
            // that delegates to MetadataRegistry with a snake_case fallback.
            // We do NOT generate it here to avoid duplicate type conflicts between
            // the library and consumer projects.
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
    /// Checks if <paramref name="type"/> is a subclass of <paramref name="recordType"/>.
    /// </summary>
    private static bool IsRecordSubclass(INamedTypeSymbol type, INamedTypeSymbol recordType)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, recordType))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Generates the per-type metadata class implementing <see cref="global::Dali.Metadata.ITypeMetadata{T}"/>.
    /// </summary>
    private static string GenerateMetadataClass(INamedTypeSymbol type, Compilation compilation)
    {
        var typeName = type.Name;
        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var ns = type.ContainingNamespace.ToDisplayString();
        var tableName = ToSnakeCase(typeName);
        var globalFullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Detect properties (search inheritance chain for Id — it's on Record base class)
        var tenantIdProp = FindProperty(type, "TenantId", "string");
        var versionProp = FindVersionProperty(type);
        var idProp = FindPropertyRecursive(type, "Id");

        var hasTenantId = tenantIdProp is not null;
        var hasVersion = versionProp is not null;

        // Check if Dali.Metadata namespace exists in compilation (for ITypeMetadata)
        // Since MetadataRegistry/ITypeMetadata is defined in Dali library, we reference via global::

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#pragma warning disable CS8669, CS8618");
        sb.AppendLine();
        sb.AppendLine("using global::Dali.Metadata;");
        sb.AppendLine("using global::SurrealDb.Net.Models;");
        sb.AppendLine();
        sb.AppendLine($"namespace Dali.Metadata;");
        sb.AppendLine();
        sb.AppendLine($"internal sealed class {typeName}Metadata : ITypeMetadata<{globalFullName}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public static readonly {typeName}Metadata Instance = new();");
        sb.AppendLine();
        sb.AppendLine($"    static {typeName}Metadata() => MetadataRegistry.Register<{globalFullName}>(Instance);");
        sb.AppendLine();
        sb.AppendLine($"    public string TableName => \"{tableName}\";");
        sb.AppendLine($"    public bool HasTenantId => {hasTenantId.ToString().ToLowerInvariant()};");
        sb.AppendLine($"    public bool HasVersion => {hasVersion.ToString().ToLowerInvariant()};");
        sb.AppendLine($"    public string? VersionFieldName => {EmitVersionFieldName(versionProp)};");
        sb.AppendLine();

        // GetTenantId
        if (hasTenantId)
        {
            sb.AppendLine($"    public string? GetTenantId({globalFullName} entity) => entity.{tenantIdProp!.Name};");
        }
        else
        {
            sb.AppendLine($"    public string? GetTenantId({globalFullName} entity) => null;");
        }

        // GetVersion / SetVersion
        if (hasVersion)
        {
            var vName = versionProp!.Name;
            sb.AppendLine($"    public long GetVersion({globalFullName} entity) => entity.{vName};");
            sb.AppendLine($"    public void SetVersion({globalFullName} entity, long version) => entity.{vName} = version;");
        }
        else
        {
            sb.AppendLine($"    public long GetVersion({globalFullName} entity) => -1;");
            sb.AppendLine($"    public void SetVersion({globalFullName} entity, long version) {{ }}");
        }

        // GetRecordId — extract the string Id from the RecordId
        sb.AppendLine($"    public string? GetRecordId({globalFullName} entity)");
        sb.AppendLine("    {");
        if (idProp is not null)
        {
            sb.AppendLine("        var id = entity.Id;");
            sb.AppendLine("        if (id is null) return null;");
            sb.AppendLine("        if (id is RecordIdOf<string> strRid) return strRid.Id;");
            sb.AppendLine("        if (id is RecordIdOf<long> longRid) return longRid.Id.ToString();");
            sb.AppendLine("        if (id is RecordIdOf<int> intRid) return intRid.Id.ToString();");
            sb.AppendLine("        return id.ToString();");
        }
        else
        {
            sb.AppendLine("        return null;");
        }
        sb.AppendLine("    }");

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static IPropertySymbol? FindProperty(INamedTypeSymbol type, string name, string propType)
    {
        return type.GetMembers(name).OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.Type.ToDisplayString() == propType);
    }

    /// <summary>
    /// Finds a property by name, walking the inheritance chain.
    /// Needed for inherited properties like <c>Id</c> on <c>Record</c>.
    /// </summary>
    private static IPropertySymbol? FindPropertyRecursive(INamedTypeSymbol? type, string name)
    {
        while (type is not null)
        {
            var prop = type.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault();
            if (prop is not null)
                return prop;
            type = type.BaseType;
        }
        return null;
    }

    private static IPropertySymbol? FindVersionProperty(INamedTypeSymbol type)
    {
        // Check for IVersioned interface
        var iVersioned = type.AllInterfaces.FirstOrDefault(i => i.Name == "IVersioned");
        if (iVersioned is not null)
        {
            var versionMember = iVersioned.GetMembers("Version").OfType<IPropertySymbol>().FirstOrDefault();
            if (versionMember is not null)
                return versionMember;
        }

        // Check for [Version] attribute on a property
        foreach (var prop in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.GetAttributes().Any(a => a.AttributeClass?.Name == "VersionAttribute"))
                return prop;
        }

        return null;
    }

    /// <summary>
    /// Emits the version field name literal for the generated code.
    /// </summary>
    private static string EmitVersionFieldName(IPropertySymbol? versionProp)
    {
        if (versionProp is not null)
            return $"\"{versionProp.Name}\"";
        return "null";
    }

    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
