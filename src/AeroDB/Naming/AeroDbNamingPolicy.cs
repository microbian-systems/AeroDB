using System.Reflection;
using AeroDB.Metadata;

namespace AeroDB;

public enum AeroDbNameCase
{
    SnakeCaseLower,
    CamelCase,
    PascalCase
}

public interface IAeroDbNamingPolicy
{
    string TableName(Type type);
    string FieldName(MemberInfo member);
    string FieldName(string clrName);
}

public sealed class AeroDbCaseNamingPolicy : IAeroDbNamingPolicy
{
    private readonly AeroDbNameCase _nameCase;

    public AeroDbCaseNamingPolicy(AeroDbNameCase nameCase)
    {
        _nameCase = nameCase;
    }

    public string TableName(Type type) => Convert(type.Name);

    public string FieldName(MemberInfo member) => FieldName(member.Name);

    public string FieldName(string clrName) => Convert(clrName);

    private string Convert(string name) => _nameCase switch
    {
        AeroDbNameCase.SnakeCaseLower => MetadataDispatch.ToSnakeCase(name),
        AeroDbNameCase.CamelCase => ToCamelCase(name),
        AeroDbNameCase.PascalCase => name,
        _ => MetadataDispatch.ToSnakeCase(name)
    };

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            return name;

        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
