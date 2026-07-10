using System.Linq.Expressions;

namespace AeroDB.Sable;

internal sealed class FilterIncludeSpec
{
    /// <summary>C# property name (e.g., "Items")</summary>
    public string PropertyName { get; set; } = "";
    /// <summary>The filter predicate expression (e.g., items => items.Any(i => i.Price > 50))</summary>
    public LambdaExpression Filter { get; set; } = null!;
}
