using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AeroDB.Analyzers;

/// <summary>
/// Rejects attribute-declared encrypted members inside server-side LINQ query operators.
/// Fluent mappings remain covered by Sable's authoritative runtime query guard.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EncryptedFieldQueryAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ADB100";
    public const string IdentityDiagnosticId = "ADB107";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Encrypted field cannot be used in a server-side query",
        "Encrypted field '{0}' cannot be used by server-side query operator '{1}'. Materialize first or use an explicit blind-index API.",
        "AeroDB.Security",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
        "Randomized encrypted values cannot be filtered, projected, sorted, grouped, indexed, or aggregated by SurrealDB.");

    private static readonly DiagnosticDescriptor UnsupportedFieldTypeRule = new(
        "ADB101",
        "Encrypted field type is not supported",
        "Encrypted field '{0}' has type '{1}'. Sable field encryption currently supports only string and byte[].",
        "AeroDB.Security",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Unsupported encrypted field types must fail at compilation instead of being persisted as plaintext.");

    private static readonly DiagnosticDescriptor InvalidBlindIndexRule = new(
        "ADB102",
        "Blind index requires an encrypted string field",
        "Blind-index field '{0}' must also have [Encrypt] and must be a string",
        "AeroDB.Security",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
        "Blind indexes are equality-leaking sidecars for reversibly encrypted string fields only.");

    private static readonly DiagnosticDescriptor InvalidEncryptionSettingRule = new(
        "ADB106",
        "Encryption attribute setting is invalid",
        "{0} setting '{1}' on field '{2}' has undefined value '{3}'",
        "AeroDB.Security",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
        "Undefined encryption and blind-index enum values must fail at compilation instead of silently selecting a default.");

    private static readonly DiagnosticDescriptor EncryptedIdentityRule = new(
        IdentityDiagnosticId,
        "Document identity cannot be encrypted",
        "Document identity field '{0}' cannot be encrypted; identities must remain clear and stable",
        "AeroDB.Security",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
        "Sable record identities are required for addressing, envelope AAD, relationships, and persistence routing, so they cannot be encrypted.");

    private static readonly ImmutableHashSet<string> ServerOperators =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "Where",
            "Select",
            "OrderBy",
            "OrderByDescending",
            "ThenBy",
            "ThenByDescending",
            "GroupBy",
            "Distinct",
            "Any",
            "All",
            "Count",
            "LongCount",
            "Min",
            "Max",
            "Sum",
            "Average",
            "First",
            "FirstOrDefault",
            "Single",
            "SingleOrDefault");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [Rule, UnsupportedFieldTypeRule, InvalidBlindIndexRule, InvalidEncryptionSettingRule, EncryptedIdentityRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeInvocation,
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
        context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        var property = (IPropertySymbol)context.Symbol;
        var encryptAttribute = GetAttribute(property, "AeroDB.Sable.EncryptAttribute");
        var blindIndexAttribute = GetAttribute(property, "AeroDB.Sable.BlindIndexAttribute");
        var encrypted = encryptAttribute is not null;
        if (encrypted && string.Equals(property.Name, "Id", StringComparison.Ordinal))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    EncryptedIdentityRule,
                    property.Locations.FirstOrDefault(),
                    property.Name));
        }

        if (blindIndexAttribute is not null
            && (!encrypted || property.Type.SpecialType != SpecialType.System_String))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidBlindIndexRule,
                    property.Locations.FirstOrDefault(),
                    property.Name));
        }

        if (encrypted
            && property.Type.SpecialType != SpecialType.System_String
            && property.Type is not IArrayTypeSymbol
            {
                ElementType.SpecialType: SpecialType.System_Byte
            })
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    UnsupportedFieldTypeRule,
                    property.Locations.FirstOrDefault(),
                    property.Name,
                    property.Type.ToDisplayString()));
        }

        if (encryptAttribute?.ConstructorArguments.FirstOrDefault() is { Kind: TypedConstantKind.Enum } algorithm
            && !IsDefinedEnumValue(algorithm))
        {
            ReportInvalidSetting(
                context,
                property,
                "[Encrypt]",
                "Algorithm",
                algorithm);
        }

        if (blindIndexAttribute is not null)
        {
            foreach (var namedArgument in blindIndexAttribute.NamedArguments)
            {
                if (namedArgument.Key is not ("Algorithm" or "Normalizer")
                    || namedArgument.Value.Kind != TypedConstantKind.Enum
                    || IsDefinedEnumValue(namedArgument.Value))
                {
                    continue;
                }

                ReportInvalidSetting(
                    context,
                    property,
                    "[BlindIndex]",
                    namedArgument.Key,
                    namedArgument.Value);
            }
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var method = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol as IMethodSymbol;
        if (method is null)
            return;

        if (IsDocumentMappingMethod(method, "EncryptField")
            || IsDocumentMappingMethod(method, "Encrypt"))
        {
            AnalyzeEncryptFieldInvocation(context, invocation);
            return;
        }

        if (IsDocumentMappingMethod(method, "Identity"))
        {
            AnalyzeIdentityInvocation(context, invocation);
            return;
        }

        if (!ServerOperators.Contains(method.Name)
            || method.ContainingType?.ToDisplayString() != "System.Linq.Queryable")
        {
            return;
        }

        var reported = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var memberAccess in invocation.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol
                is not IPropertySymbol property
                || !HasEncryptAttribute(property)
                || !reported.Add(property))
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(Rule, memberAccess.Name.GetLocation(), property.Name, method.Name));
        }
    }

    private static void AnalyzeEncryptFieldInvocation(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation)
    {
        var encryptedSelector = GetSelectedProperty(context, invocation);
        if (encryptedSelector is null)
            return;

        var (property, location) = encryptedSelector.Value;
        var isIdentity = string.Equals(property.Name, "Id", StringComparison.Ordinal)
            || ReceiverContainsSelector(
                context,
                invocation,
                "Identity",
                property);
        if (isIdentity)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(EncryptedIdentityRule, location, property.Name));
        }
    }

    private static void AnalyzeIdentityInvocation(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation)
    {
        var identitySelector = GetSelectedProperty(context, invocation);
        if (identitySelector is null)
            return;

        var (identityProperty, _) = identitySelector.Value;
        var encryptedSelector = FindReceiverSelector(
            context,
            invocation,
            ["EncryptField", "Encrypt"],
            identityProperty);
        if (encryptedSelector is { } selector)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    EncryptedIdentityRule,
                    selector.Location,
                    identityProperty.Name));
        }
    }

    private static bool IsDocumentMappingMethod(IMethodSymbol method, string methodName)
        => string.Equals(method.Name, methodName, StringComparison.Ordinal)
            && string.Equals(
                method.ContainingType?.Name,
                "DocumentMapping",
                StringComparison.Ordinal)
            && string.Equals(
                method.ContainingNamespace?.ToDisplayString(),
                "AeroDB.Sable",
                StringComparison.Ordinal);

    private static (
        IPropertySymbol Property,
        Location Location)? GetSelectedProperty(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count == 0)
            return null;

        foreach (var memberAccess in invocation.ArgumentList.Arguments[0]
                     .Expression
                     .DescendantNodesAndSelf()
                     .OfType<MemberAccessExpressionSyntax>())
        {
            if (context.SemanticModel.GetSymbolInfo(
                    memberAccess,
                    context.CancellationToken).Symbol is IPropertySymbol property)
            {
                return (property, memberAccess.Name.GetLocation());
            }
        }

        return null;
    }

    private static bool ReceiverContainsSelector(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        string methodName,
        IPropertySymbol expectedProperty)
        => FindReceiverSelector(
            context,
            invocation,
            [methodName],
            expectedProperty) is not null;

    private static (
        IPropertySymbol Property,
        Location Location)? FindReceiverSelector(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        IReadOnlyCollection<string> methodNames,
        IPropertySymbol expectedProperty)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Expression: { } receiver
            })
        {
            return null;
        }

        foreach (var receiverInvocation in receiver
                     .DescendantNodesAndSelf()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (context.SemanticModel.GetSymbolInfo(
                    receiverInvocation,
                    context.CancellationToken).Symbol is not IMethodSymbol receiverMethod
                || !methodNames.Contains(receiverMethod.Name)
                || !IsDocumentMappingMethod(receiverMethod, receiverMethod.Name))
            {
                continue;
            }

            var selector = GetSelectedProperty(context, receiverInvocation);
            if (selector is { } selected
                && SymbolEqualityComparer.Default.Equals(
                    selected.Property,
                    expectedProperty))
            {
                return selected;
            }
        }

        return null;
    }

    private static bool HasEncryptAttribute(IPropertySymbol property)
        => GetAttribute(property, "AeroDB.Sable.EncryptAttribute") is not null;

    private static AttributeData? GetAttribute(IPropertySymbol property, string fullName)
        => property.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == fullName);

    private static bool IsDefinedEnumValue(TypedConstant constant)
        => constant.Type is INamedTypeSymbol enumType
            && enumType.GetMembers()
                .OfType<IFieldSymbol>()
                .Any(field =>
                    field.HasConstantValue
                    && Equals(field.ConstantValue, constant.Value));

    private static void ReportInvalidSetting(
        SymbolAnalysisContext context,
        IPropertySymbol property,
        string attributeName,
        string settingName,
        TypedConstant value)
        => context.ReportDiagnostic(
            Diagnostic.Create(
                InvalidEncryptionSettingRule,
                property.Locations.FirstOrDefault(),
                attributeName,
                settingName,
                property.Name,
                value.Value?.ToString() ?? "<null>"));
}
