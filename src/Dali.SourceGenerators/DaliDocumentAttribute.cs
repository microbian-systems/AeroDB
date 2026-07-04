namespace Dali;

/// <summary>
/// Marks a type for AeroDB source generation. Use as an opt-out on Record subclasses
/// (to skip generation) or as an opt-in on non-Record types.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class DaliDocumentAttribute : System.Attribute
{
    public bool SkipGeneration { get; set; }
}
