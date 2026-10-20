using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Linq;

namespace AeroDB.SourceGenerators;

/// <summary>
/// Generates CBOR-compatible shim <c>Record</c> types for <c>SableDocument&lt;TId&gt;</c> subclasses.
/// Enables <c>LoadAsync&lt;T&gt;()</c> via the <c>DeserializeViaShimAsync&lt;T&gt;()</c> path:
/// the shim is deserialized by the SurrealDB SDK (CBOR), then materialized to the entity via <c>ToEntity()</c>.
/// </summary>
[Generator]
public class AeroDBEntityShimGenerator : IIncrementalGenerator
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

            // Find the ISableDocument<TId> type in AeroDB.Sable
            var entityInterfaceType = compilation.GetTypeByMetadataName("AeroDB.Sable.ISableDocument`1");
            if (entityInterfaceType is null) return;

            // Find the Record type in SurrealDb.Net (needed for shim base class)
            var recordType = compilation.GetTypeByMetadataName("SurrealDb.Net.Models.Record");
            if (recordType is null) return;

            // Optionally find the AeroDBDocumentAttribute for opt-out
            var AeroDBDocAttrType = compilation.GetTypeByMetadataName("AeroDB.Sable.AeroDBDocumentAttribute");

            foreach (var type in types)
            {
                if (type is null) continue;
                if (type.IsAbstract) continue;
                if (!IsEntitySubclass(type, entityInterfaceType)) continue;

                // Check for [AeroDBDocument(SkipGeneration = true)] opt-out
                if (AeroDBDocAttrType is not null)
                {
                    var skipAttr = type.GetAttributes()
                        .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, AeroDBDocAttrType));
                    if (skipAttr is not null)
                    {
                        var skipNamedArg = skipAttr.NamedArguments
                            .FirstOrDefault(kv => kv.Key == "SkipGeneration");
                        if (skipNamedArg.Key == "SkipGeneration" && skipNamedArg.Value.Value is true)
                            continue;
                    }
                }

                var idType = GetEntityIdType(type, entityInterfaceType);
                var supportsGeneratedMaterializers = compilation.GetTypeByMetadataName(
                    "AeroDB.Sable.Metadata.GeneratedMaterializer`1") is not null;
                var sourceText = GenerateShimClass(type, idType, supportsGeneratedMaterializers);
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
    /// Checks if <paramref name="type"/> implements <c>ISableDocument&lt;TId&gt;</c>.
    /// </summary>
    private static bool IsEntitySubclass(INamedTypeSymbol type, INamedTypeSymbol entityInterfaceType)
    {
        return type.AllInterfaces.Any(i =>
            i.IsGenericType &&
            SymbolEqualityComparer.Default.Equals(i.ConstructedFrom, entityInterfaceType));
    }

    /// <summary>
    /// Extracts the <c>TId</c> type argument from <c>ISableDocument&lt;TId&gt;</c> in the interface chain.
    /// </summary>
    private static ITypeSymbol? GetEntityIdType(INamedTypeSymbol type, INamedTypeSymbol entityInterfaceType)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(iface.ConstructedFrom, entityInterfaceType))
                return iface.TypeArguments[0];
        }
        return null;
    }

    /// <summary>
    /// Generates the shim class source for the given entity type.
    /// The shim extends <c>Record</c> and mirrors writable properties with <c>CborPropertyAttribute</c>.
    /// </summary>
    private static string GenerateShimClass(
        INamedTypeSymbol type,
        ITypeSymbol? idType,
        bool supportsGeneratedMaterializers)
    {
        // A required/init-only member makes the legacy shim's `new T()` invalid
        // as well. Emit no shim for that concrete root and leave it on the
        // established POCO path rather than producing uncompilable source.
        if (HasRequiredOrInitMember(type))
            return string.Empty;

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
        sb.AppendLine("namespace AeroDB.Sable.Metadata;");
        sb.AppendLine();
        sb.AppendLine($"internal sealed class {typeName}Shim : Record");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.AppendLine($"        global::AeroDB.Sable.Metadata.MetadataRegistry.RegisterShimType<{fullName}>(typeof({typeName}Shim));");
        var generatesMaterializer = supportsGeneratedMaterializers && CanGenerateMaterializer(type);
        if (generatesMaterializer)
            sb.AppendLine($"        global::AeroDB.Sable.Metadata.MetadataRegistry.RegisterGeneratedMaterializer<{fullName}>(Materialize);");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Gather public read/write properties (excluding Id — handled by Record base)
        var properties = new List<string>();
        var persistentProperties = GetPersistentProperties(type);
        foreach (var member in persistentProperties)
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

        if (generatesMaterializer)
        {
            sb.AppendLine();
            EmitMaterializerMethod(sb, type, "Materialize", root: true);
            var embeddedTypes = new List<INamedTypeSymbol>();
            CollectEmbeddedTypes(type, embeddedTypes, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
            foreach (var embedded in embeddedTypes)
            {
                sb.AppendLine();
                EmitMaterializerMethod(sb, embedded, EmbeddedMethodName(embedded), root: false);
            }
        }
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static IReadOnlyList<IPropertySymbol> GetPersistentProperties(INamedTypeSymbol type)
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            hierarchy.Push(current);

        var properties = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        foreach (var current in hierarchy)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (IsJsonIgnored(property))
                    continue;

                if (property.DeclaredAccessibility != Accessibility.Public
                    || property.IsStatic
                    || property.GetMethod?.DeclaredAccessibility != Accessibility.Public
                    || property.SetMethod?.DeclaredAccessibility != Accessibility.Public
                    || property.SetMethod.IsInitOnly
                    || property.Parameters.Length != 0)
                    continue;

                properties[property.Name] = property;
            }
        }

        return properties.Values.ToArray();
    }

    private static bool CanGenerateMaterializer(INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)
            || type.InstanceConstructors.All(ctor => ctor.Parameters.Length != 0 || ctor.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)))
            return false;

        if (HasCustomSerialization(type) || HasUnsupportedPersistentMember(type))
            return false;

        var visiting = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        return GetPersistentProperties(type).All(property => IsSupportedMaterializerType(property.Type, visiting));
    }

    private static bool IsSupportedMaterializerType(ITypeSymbol type, HashSet<ITypeSymbol> visiting)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            return IsSupportedMaterializerType(nullable.TypeArguments[0], visiting);

        if (type.SpecialType is SpecialType.System_String
            or SpecialType.System_Boolean
            or SpecialType.System_Byte
            or SpecialType.System_SByte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_Decimal)
            return true;

        if (type.TypeKind == TypeKind.Enum)
            return type is not INamedTypeSymbol enumType || !HasCustomSerialization(enumType);

        if (type.ToDisplayString() is "System.Guid" or "System.DateTime" or "System.DateTimeOffset")
            return true;

        if (type is IArrayTypeSymbol { Rank: 1 } array)
            return !IsCollectionType(array.ElementType)
                && IsSupportedMaterializerType(array.ElementType, visiting);

        if (type is INamedTypeSymbol named
            && named.IsGenericType
            && named.TypeArguments.Length == 1
            && named.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IReadOnlyList<T>")
            return !IsCollectionType(named.TypeArguments[0])
                && IsSupportedMaterializerType(named.TypeArguments[0], visiting);

        if (type is not INamedTypeSymbol embedded
            || embedded.TypeKind != TypeKind.Class
            || embedded.IsAbstract
            || embedded.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)
            || embedded.InstanceConstructors.All(ctor => ctor.Parameters.Length != 0 || ctor.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            || HasCustomSerialization(embedded)
            || HasUnsupportedPersistentMember(embedded)
            || !visiting.Add(type))
            return false;

        var supported = GetPersistentProperties(embedded).Count > 0
            && GetPersistentProperties(embedded).All(property => IsSupportedMaterializerType(property.Type, visiting));
        visiting.Remove(type);
        return supported;
    }

    private static bool HasUnsupportedPersistentMember(INamedTypeSymbol type)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || IsJsonIgnored(property))
                    continue;

                if (HasConditionalJsonIgnore(property)
                    || HasCustomSerialization(property)
                    || property.Parameters.Length != 0
                    || property.IsRequired
                    || property.DeclaredAccessibility != Accessibility.Public
                    || property.GetMethod?.DeclaredAccessibility != Accessibility.Public
                    || property.SetMethod?.DeclaredAccessibility != Accessibility.Public
                    || property.SetMethod.IsInitOnly)
                    return true;

                if (property.DeclaredAccessibility is not Accessibility.Public
                    && IsHiddenByBaseMember(property, current.BaseType))
                    return true;
            }
        }

        return false;
    }

    private static bool HasRequiredOrInitMember(INamedTypeSymbol type)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (current.GetMembers().OfType<IPropertySymbol>().Any(property =>
                    !property.IsStatic
                    && !IsJsonIgnored(property)
                    && (property.IsRequired || property.SetMethod?.IsInitOnly == true)))
                return true;
        }

        return false;
    }

    private static bool IsHiddenByBaseMember(IPropertySymbol property, INamedTypeSymbol? baseType)
    {
        for (var current = baseType; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (current.GetMembers(property.Name).OfType<IPropertySymbol>().Any(candidate => !candidate.IsStatic && !IsJsonIgnored(candidate)))
                return true;
        }

        return false;
    }

    private static bool IsCollectionType(ITypeSymbol type)
        => type is IArrayTypeSymbol
            || type is INamedTypeSymbol named
                && named.IsGenericType
                && named.TypeArguments.Length == 1
                && named.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IReadOnlyList<T>";

    private static bool IsJsonIgnored(IPropertySymbol property)
    {
        var attribute = GetJsonIgnoreAttribute(property);
        if (attribute is null)
            return false;

        var condition = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "Condition");
        // JsonIgnoreCondition.Never is zero; only the default/Always mode is
        // safe to omit from a generated reader.
        return condition.Key is null || condition.Value.Value is null or 1;
    }

    private static bool HasConditionalJsonIgnore(IPropertySymbol property)
    {
        var attribute = GetJsonIgnoreAttribute(property);
        if (attribute is null)
            return false;

        var condition = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "Condition");
        return condition.Key is not null && condition.Value.Value is not null and not 1;
    }

    private static AttributeData? GetJsonIgnoreAttribute(IPropertySymbol property)
        => property.GetAttributes().FirstOrDefault(candidate =>
            candidate.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute");

    private static bool HasCustomSerialization(ISymbol symbol)
        => symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.Name is "JsonConverterAttribute" or "JsonPropertyNameAttribute");

    private static void EmitMaterializerMethod(StringBuilder sb, INamedTypeSymbol type, string methodName, bool root)
    {
        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        sb.AppendLine($"    private static {fullName} {methodName}(");
        sb.AppendLine("        global::System.Collections.Generic.IReadOnlyDictionary<string, object?> values,");
        sb.AppendLine("        global::AeroDB.Sable.SchemaOptions? schema,");
        sb.AppendLine("        global::AeroDB.Sable.EnumStorage enumStorage)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var entity = new {fullName}();");
        foreach (var property in GetPersistentProperties(type))
            EmitPropertyAssignment(sb, fullName, property, root);
        sb.AppendLine("        return entity;");
        sb.AppendLine("    }");
    }

    private static void EmitPropertyAssignment(
        StringBuilder sb,
        string ownerName,
        IPropertySymbol property,
        bool root)
    {
        var propertyType = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var valueName = "value_" + property.Name;
        if (root && property.Name == "Id")
        {
            sb.AppendLine($"        if (global::AeroDB.Sable.Metadata.MetadataRegistry.TryReadIdentity<{propertyType}>(values, typeof({ownerName}), schema, out var {valueName}))");
            sb.AppendLine($"            entity.{property.Name} = {valueName};");
            return;
        }

        if (TryGetCollectionElement(property.Type, out var element, out var list))
        {
            var elementExpression = EmitCollectionElementExpression(element, "item");
            var materialized = list
                ? $"global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Select(global::System.Linq.Enumerable.Cast<object?>((global::System.Collections.IEnumerable)raw_{property.Name}), item => {elementExpression}))"
                : $"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select(global::System.Linq.Enumerable.Cast<object?>((global::System.Collections.IEnumerable)raw_{property.Name}), item => {elementExpression}))";
            sb.AppendLine($"        if (global::AeroDB.Sable.Metadata.MetadataRegistry.TryReadRawValue(values, typeof({ownerName}), \"{Escape(property.Name)}\", schema, out var raw_{property.Name}))");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (raw_{property.Name} is null)");
            sb.AppendLine($"                entity.{property.Name} = null!;");
            sb.AppendLine($"            else if (raw_{property.Name} is global::System.Collections.IEnumerable && raw_{property.Name} is not string)");
            sb.AppendLine($"                entity.{property.Name} = {materialized};");
            sb.AppendLine("            else");
            sb.AppendLine($"                throw new global::System.InvalidOperationException(\"Expected a one-dimensional sequence for '{Escape(property.Name)}'.\");");
            sb.AppendLine("        }");
            return;
        }

        if (property.Type is INamedTypeSymbol { TypeKind: TypeKind.Class } embedded
            && embedded.SpecialType != SpecialType.System_String)
        {
            sb.AppendLine($"        if (global::AeroDB.Sable.Metadata.MetadataRegistry.TryReadRawValue(values, typeof({ownerName}), \"{Escape(property.Name)}\", schema, out var {valueName}))");
            sb.AppendLine("        {");
            sb.AppendLine($"            if ({valueName} is null)");
            sb.AppendLine($"                entity.{property.Name} = null!;");
            sb.AppendLine($"            else if ({valueName} is global::System.Collections.Generic.IReadOnlyDictionary<string, object?> map_{property.Name})");
            sb.AppendLine($"                entity.{property.Name} = {EmbeddedMethodName(embedded)}(map_{property.Name}, schema, enumStorage);");
            sb.AppendLine("            else");
            sb.AppendLine($"                throw new global::System.InvalidOperationException(\"Expected an embedded record map for '{Escape(property.Name)}'.\");");
            sb.AppendLine("        }");
            return;
        }

        sb.AppendLine($"        if (global::AeroDB.Sable.Metadata.MetadataRegistry.TryReadValue<{propertyType}>(values, typeof({ownerName}), \"{Escape(property.Name)}\", schema, enumStorage, out var {valueName}))");
        sb.AppendLine($"            entity.{property.Name} = {valueName};");
    }

    private static string EmitCollectionElementExpression(ITypeSymbol element, string variable)
    {
        if (element is INamedTypeSymbol { TypeKind: TypeKind.Class } embedded
            && embedded.SpecialType != SpecialType.System_String)
        {
            return $"{EmbeddedMethodName(embedded)}({variable} as global::System.Collections.Generic.IReadOnlyDictionary<string, object?> ?? throw new global::System.InvalidOperationException(\"Expected embedded record map.\"), schema, enumStorage)";
        }

        var type = element.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"global::AeroDB.Sable.Metadata.MetadataRegistry.ReadScalar<{type}>({variable}, enumStorage)";
    }

    private static bool TryGetCollectionElement(ITypeSymbol type, out ITypeSymbol element, out bool list)
    {
        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            element = array.ElementType;
            list = false;
            return true;
        }

        if (type is INamedTypeSymbol named && named.IsGenericType && named.TypeArguments.Length == 1
            && named.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IReadOnlyList<T>")
        {
            element = named.TypeArguments[0];
            list = named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>";
            return true;
        }

        element = default!;
        list = false;
        return false;
    }

    private static void CollectEmbeddedTypes(
        INamedTypeSymbol type,
        List<INamedTypeSymbol> result,
        HashSet<ITypeSymbol> visited)
    {
        foreach (var property in GetPersistentProperties(type))
        {
            var candidate = TryGetCollectionElement(property.Type, out var element, out _)
                ? element
                : property.Type;
            if (candidate is not INamedTypeSymbol { TypeKind: TypeKind.Class } embedded
                || embedded.SpecialType == SpecialType.System_String
                || !visited.Add(embedded))
                continue;

            result.Add(embedded);
            CollectEmbeddedTypes(embedded, result, visited);
        }
    }

    private static string EmbeddedMethodName(INamedTypeSymbol type)
    {
        var identity = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var builder = new StringBuilder("MaterializeEmbedded_");
        foreach (var character in identity)
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');

        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
