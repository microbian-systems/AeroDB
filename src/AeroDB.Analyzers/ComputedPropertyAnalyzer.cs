using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AeroDB.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ComputedPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ADB001";

    private static readonly LocalizableString Title = "Computed property on SableDocument subclass";
    private static readonly LocalizableString MessageFormat = "Computed property '{0}' on SableDocument subclass will be serialized but not included in SurrealDB schema. Add [JsonIgnore] to prevent runtime errors.";
    private static readonly LocalizableString Description = "Properties with only a getter (no setter) on SableDocument subclasses are excluded from schema generation but still serialized to SurrealDB, causing 'no such field exists' errors at runtime.";

    private const string Category = "AeroDB.Schema";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId, Title, MessageFormat, Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;

        if (!IsSableDocumentSubclass(namedType))
            return;

        foreach (var member in namedType.GetMembers())
        {
            if (member is not IPropertySymbol property) continue;
            if (property.IsStatic) continue;
            if (property.DeclaredAccessibility != Accessibility.Public) continue;

            // Has getter but no setter at all (expression-bodied: =>, or { get; })
            if (property.GetMethod is not null && property.SetMethod is null)
            {
                if (HasJsonIgnoreAttribute(property))
                    continue;

                var diagnostic = Diagnostic.Create(Rule, property.Locations[0], property.Name);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private static bool IsSableDocumentSubclass(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == "SableDocument" && current.ContainingNamespace?.Name == "Sable")
                return true;
            current = current.BaseType;
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.Name == "ISableDocument" && iface.IsGenericType)
                return true;
        }

        return false;
    }

    private static bool HasJsonIgnoreAttribute(IPropertySymbol property)
    {
        foreach (var attr in property.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null) continue;

            var fullName = attrClass.ToDisplayString();
            if (fullName is "System.Text.Json.Serialization.JsonIgnoreAttribute"
                or "Newtonsoft.Json.JsonIgnoreAttribute"
                or "System.Text.Json.Serialization.JsonIgnore")
            {
                return true;
            }
        }

        return false;
    }
}
