using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace AeroDB.SourceGenerators;

[Generator]
public class AeroDBDocumentGenerator : IIncrementalGenerator
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

            // Find the Record type in SurrealDb.Net and the SableDocument<TId> type in AeroDB.Sable
            var recordType = compilation.GetTypeByMetadataName("SurrealDb.Net.Models.Record");
            var entityGenericType = compilation.GetTypeByMetadataName("AeroDB.Sable.SableDocument`1");
            if (recordType is null && entityGenericType is null)
                return;

            // Optionally find the AeroDBDocumentAttribute — if it's not available
            // in the compilation (e.g., consumer doesn't reference the attribute assembly),
            // we skip the opt-out check and generate for all Record subclasses.
            var AeroDBDocAttrType = compilation.GetTypeByMetadataName("AeroDB.Sable.AeroDBDocumentAttribute");

            var validTypes = new List<INamedTypeSymbol>();

            foreach (var type in types)
            {
                if (type is null) continue;
                if (type.IsAbstract) continue;

                bool isRecord = recordType is not null && IsRecordSubclass(type, recordType);
                bool isEntity = entityGenericType is not null && IsEntitySubclass(type, entityGenericType);
                if (!isRecord && !isEntity) continue;

                // Check for [AeroDBDocument(SkipGeneration = true)] — opt-out
                if (AeroDBDocAttrType is not null)
                {
                    var skipAttr = type.GetAttributes()
                        .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, AeroDBDocAttrType));
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
                bool isEntity = entityGenericType is not null && IsEntitySubclass(type, entityGenericType);
                var sourceText = GenerateMetadataClass(type, compilation, isEntity);
                var hintName = $"{type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)).Replace("global::", "").Replace(".", "_")}.Metadata.g.cs";
                ctx.AddSource(hintName, sourceText);
            }

            var relationshipCatalog = GenerateRelationshipCatalog(validTypes);
            if (relationshipCatalog is not null)
                ctx.AddSource("AeroDB.Sable.GeneratedRelationshipCatalog.g.cs", relationshipCatalog);

            // NOTE: MetadataDispatch is a hand-written class in src/AeroDB.Sable/Metadata/
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
    /// Checks if <paramref name="type"/> is a subclass of <c>SableDocument&lt;TId&gt;</c>.
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
    /// Generates the per-type metadata class implementing <c>ITypeMetadata&lt;T&gt;</c>.
    /// </summary>
    private static string GenerateMetadataClass(INamedTypeSymbol type, Compilation compilation, bool isEntity)
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
        var hasDocumentMetadata = type.AllInterfaces.Any(i => i.Name == "IDocumentMetadata");
        var hasEventProjectionDispatch = false; // Detected by EventProjection subclass analysis (deferred)

        // Check if AeroDB.Sable.Metadata namespace exists in compilation (for ITypeMetadata)
        // Since MetadataRegistry/ITypeMetadata is defined in AeroDB.Sable library, we reference via global::

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#pragma warning disable CS8669, CS8618");
        sb.AppendLine();
        sb.AppendLine("using global::System;");
        sb.AppendLine("using global::AeroDB.Sable.Metadata;");
        if (!isEntity)
            sb.AppendLine("using global::SurrealDb.Net.Models;");
        sb.AppendLine();
        sb.AppendLine($"namespace AeroDB.Sable.Metadata;");
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
        sb.AppendLine($"    public bool HasDocumentMetadata => {hasDocumentMetadata.ToString().ToLowerInvariant()};");
        sb.AppendLine($"    public bool HasEventProjectionDispatch => {hasEventProjectionDispatch.ToString().ToLowerInvariant()};");
        sb.AppendLine($"    public string? VersionFieldName => {EmitVersionFieldName(versionProp)};");
        sb.AppendLine($"    public Func<object, long>? GetVersionAccessor => {EmitGetVersionAccessor(versionProp, globalFullName)};");
        sb.AppendLine($"    public Action<object, long>? SetVersionAccessor => {EmitSetVersionAccessor(versionProp, globalFullName)};");
        sb.AppendLine();

        // GetTenantId
        if (hasTenantId)
        {
            sb.AppendLine($"    public string? GetTenantId({globalFullName} entity) => entity.{tenantIdProp!.Name};");
            sb.AppendLine($"    public void SetTenantId({globalFullName} entity, string? tenantId) => entity.{tenantIdProp!.Name} = tenantId;");
        }
        else
        {
            sb.AppendLine($"    public string? GetTenantId({globalFullName} entity) => null;");
            sb.AppendLine($"    public void SetTenantId({globalFullName} entity, string? tenantId) {{ }}");
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

        // GetRecordId — extract the string Id from the RecordId or entity typed Id
        if (isEntity)
        {
            if (idProp is not null && idProp.Type.IsValueType)
                sb.AppendLine($"    public string? GetRecordId({globalFullName} entity) => entity.Id.ToString();");
            else
                sb.AppendLine($"    public string? GetRecordId({globalFullName} entity) => entity.Id?.ToString();");
        }
        else if (idProp is not null)
        {
            sb.AppendLine($"    public string? GetRecordId({globalFullName} entity)");
            sb.AppendLine("    {");
            sb.AppendLine("        var id = entity.Id;");
            sb.AppendLine("        if (id is null) return null;");
            sb.AppendLine("        if (id is RecordIdOf<string> strRid) return strRid.Id;");
            sb.AppendLine("        if (id is RecordIdOf<long> longRid) return longRid.Id.ToString();");
            sb.AppendLine("        if (id is RecordIdOf<int> intRid) return intRid.Id.ToString();");
            sb.AppendLine("        return id.ToString();");
            sb.AppendLine("    }");
        }
        else
        {
            sb.AppendLine($"    public string? GetRecordId({globalFullName} entity) => null;");
        }

        if (isEntity)
        {
            if (idProp is not null && idProp.Type.IsValueType)
                sb.AppendLine($"    public Func<object, string?>? GetRecordIdAccessor => obj => (({globalFullName})obj).Id.ToString();");
            else
                sb.AppendLine($"    public Func<object, string?>? GetRecordIdAccessor => obj => (({globalFullName})obj).Id?.ToString();");
        }
        else if (idProp is not null)
            sb.AppendLine($"    public Func<object, string?>? GetRecordIdAccessor => {EmitGetRecordIdAccessor(idProp, globalFullName)};");
        else
            sb.AppendLine("    public Func<object, string?>? GetRecordIdAccessor => null;");

        // Emit FieldSchema list for compile-time schema generation
        var fields = new List<string>();
        foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.Name == "Id") continue;
            if (member.DeclaredAccessibility != Accessibility.Public) continue;
            if (member.IsStatic) continue;
            if (member.GetMethod is null || member.SetMethod is null) continue;

            var surrealType = GetSurrealType(member);
            var escapedType = surrealType.Replace("\"", "\\\"");
            fields.Add($"            new global::AeroDB.Sable.Metadata.FieldSchema(\"{member.Name}\", \"{escapedType}\", true, true)");
        }

        if (fields.Count > 0)
        {
            sb.AppendLine($"    public System.Collections.Generic.IReadOnlyList<global::AeroDB.Sable.Metadata.FieldSchema>? Fields =>");
            sb.AppendLine("        new global::AeroDB.Sable.Metadata.FieldSchema[]");
            sb.AppendLine("        {");
            sb.Append(string.Join(",\n", fields));
            sb.AppendLine();
            sb.AppendLine("        };");
        }
        else
        {
            sb.AppendLine("    public System.Collections.Generic.IReadOnlyList<global::AeroDB.Sable.Metadata.FieldSchema>? Fields => null;");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string? GenerateRelationshipCatalog(IReadOnlyList<INamedTypeSymbol> validTypes)
    {
        var candidates = new List<string>();
        foreach (var sourceType in validTypes)
        {
            foreach (var property in sourceType.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.DeclaredAccessibility != Accessibility.Public) continue;
                if (property.IsStatic) continue;
                if (property.GetMethod is null || property.SetMethod is null) continue;
                if (!property.Name.EndsWith("Id") || property.Name.Length <= 2) continue;

                var targetName = property.Name.Substring(0, property.Name.Length - 2);
                var targetMatches = validTypes.Where(t => t.Name == targetName).ToList();
                if (targetMatches.Count != 1)
                    continue;

                var target = targetMatches[0];
                var targetId = FindPropertyRecursive(target, "Id");
                if (targetId is null || !TypesAreCompatible(property.Type, targetId.Type))
                    continue;

                candidates.Add(
                    $"            new global::AeroDB.Sable.RelationshipCandidate(\"{Escape(sourceType.Name)}\", \"{Escape(target.Name)}\", \"{Escape(property.Name)}\", \"{Escape(targetId.Name)}\", global::AeroDB.Sable.RelationshipStorageKind.ScalarForeignKey, global::AeroDB.Sable.RelationshipCardinality.One, false)");
            }
        }

        if (candidates.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#pragma warning disable CS8669, CS8618");
        sb.AppendLine();
        sb.AppendLine("namespace AeroDB.Sable.Metadata;");
        sb.AppendLine();
        sb.AppendLine("internal static class GeneratedRelationshipCatalog");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.AppendLine("        global::AeroDB.Sable.Metadata.MetadataRegistry.RegisterRelationshipCandidates(");
        sb.Append(string.Join(",\n", candidates));
        sb.AppendLine();
        sb.AppendLine("        );");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static bool TypesAreCompatible(ITypeSymbol sourceType, ITypeSymbol targetType)
    {
        if (SymbolEqualityComparer.Default.Equals(sourceType, targetType))
            return true;

        if (sourceType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } sourceNullable)
            return SymbolEqualityComparer.Default.Equals(sourceNullable.TypeArguments[0], targetType);

        if (targetType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } targetNullable)
            return SymbolEqualityComparer.Default.Equals(sourceType, targetNullable.TypeArguments[0]);

        return false;
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static IPropertySymbol? FindProperty(INamedTypeSymbol type, string name, string propType)
    {
        return type.GetMembers(name).OfType<IPropertySymbol>()
            .FirstOrDefault(p =>
                p.Type.ToDisplayString() == propType ||
                // With nullable enabled, string? displays as "string?" — handle both
                p.Type.ToDisplayString() == propType + "?");
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

    private static string EmitGetVersionAccessor(IPropertySymbol? versionProp, string globalFullName)
    {
        if (versionProp is not null)
            return $"obj => (({globalFullName})obj).{versionProp.Name}";
        return "null";
    }

    private static string EmitSetVersionAccessor(IPropertySymbol? versionProp, string globalFullName)
    {
        if (versionProp is not null)
            return $"(obj, v) => (({globalFullName})obj).{versionProp.Name} = v";
        return "null";
    }

    private static string EmitGetRecordIdAccessor(IPropertySymbol? idProp, string globalFullName)
    {
        if (idProp is null) return "null";
        return "obj =>\n    {\n" +
               $"        var entity = ({globalFullName})obj;\n" +
               "        var id = entity.Id;\n" +
               "        if (id is null) return null;\n" +
               "        if (id is RecordIdOf<string> strRid) return strRid.Id;\n" +
               "        if (id is RecordIdOf<long> longRid) return longRid.Id.ToString();\n" +
               "        if (id is RecordIdOf<int> intRid) return intRid.Id.ToString();\n" +
               "        return id.ToString();\n" +
               "    }";
    }

    private static string GetSurrealType(IPropertySymbol property)
    {
        var type = property.Type;
        var isNullable = IsNullableProperty(property);
        var nullableUnderlying = GetNullableUnderlyingType(type);
        var effectiveType = nullableUnderlying ?? type;

        var surrealType = GetRequiredSurrealType(effectiveType);
        return isNullable ? $"option<{surrealType}>" : surrealType;
    }

    private static string GetRequiredSurrealType(ITypeSymbol type)
    {
        var name = type.ToDisplayString();
        // Nullable reference types (e.g., string?) display with a trailing '?' even
        // though they are not Nullable<T>. Strip the annotation for type-mapping.
        if (name.EndsWith("?") && !type.IsValueType)
            name = name.Substring(0, name.Length - 1);
        return name switch
        {
            "AeroDB.Sable.GeometryPoint" or "global::AeroDB.Sable.GeometryPoint" or "GeometryPoint" => "geometry",
            "AeroDB.Sable.GeometryPolygon" or "global::AeroDB.Sable.GeometryPolygon" or "GeometryPolygon" => "geometry",
            "string" or "System.Guid" => "string",
            "long" or "int" or "short" or "byte" or "System.Int64" or "System.Int32" or "System.Int16" or "System.Byte" => "int",
            "float" or "double" or "decimal" or "System.Single" or "System.Double" or "System.Decimal" => "float",
            "bool" or "System.Boolean" => "bool",
            "System.DateTime" or "System.DateTimeOffset" => "datetime",
            "byte[]" or "System.Byte[]" => "bytes",
            _ when type is IArrayTypeSymbol => "array",
            _ when type.OriginalDefinition?.ToDisplayString() == "System.Collections.Generic.List<T>" => "array",
            _ when type.TypeKind == TypeKind.Enum => BuildEnumLiteralType(type),
            _ => "object"
        };
    }

    private static string BuildEnumLiteralType(ITypeSymbol enumType)
    {
        var names = enumType.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue && f.Name != "value__")
            .Select(f => $"\"{f.Name}\"");
        return string.Join(" | ", names);
    }

    private static bool IsNullableProperty(IPropertySymbol property)
    {
        if (GetNullableUnderlyingType(property.Type) is not null)
            return true;

        // Option B: reference types are nullable by default in C# — they can always be null at runtime.
        // Only emit a non-nullable SurrealDB TYPE (without option<>) when [Required] is explicitly present.
        if (!property.Type.IsValueType)
            return !HasRequiredAttribute(property);

        return false; // value types are never nullable by default
    }

    private static bool HasRequiredAttribute(IPropertySymbol property)
    {
        return property.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.RequiredAttribute");
    }

    private static ITypeSymbol? GetNullableUnderlyingType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
        {
            return named.TypeArguments[0];
        }

        return null;
    }

    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return string.Concat(name.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
    }
}
