namespace AeroDB;

/// <summary>
/// Applied to a <c>long</c> property to indicate it is the concurrency version field.
/// Takes precedence over <see cref="IVersioned"/> when both are present on the same type.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class VersionAttribute : Attribute { }
