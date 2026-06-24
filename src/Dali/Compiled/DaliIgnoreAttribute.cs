namespace Dali;

/// <summary>
/// Marks a property on a compiled query class that should be ignored during
/// parameter discovery. Properties decorated with this attribute are not
/// mapped to SQL parameters.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class DaliIgnoreAttribute : Attribute
{
}
